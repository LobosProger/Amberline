using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    /// <summary>
    /// Stops the run and asks the person a question, then hands their answer back as the tool
    /// result. The way out of a task the model cannot finish because it was never told enough.
    /// </summary>
    /// <remarks>
    /// The answer enters the transcript as an ordinary tool response, so the run carries on with
    /// the same history and the same budget. Nothing about this tool is a second conversation.
    /// <para>
    /// Not mutating and not a command, so the approval gate never sees it - there is nothing to
    /// approve about being asked something. What keeps it from becoming an interrogation is the
    /// per-run limit below, and the fact that a run which has only asked questions still counts as
    /// having done nothing, so finish is refused after it exactly as it would be after no calls.
    /// </para>
    /// </remarks>
    public class AskUserTool : IToolExecutor
    {
        readonly Func<string, CancellationToken, UniTask<string>> _askTheUserAsync;

        int _questionsAskedInThisRun;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "ask_user",
            parameterNames: new[] { "question" },
            isMutating: false,
            isCommand: false,
            maximumResponseTokens: 512,
            parameterNamesWithASafeDefault: null,
            // A person's answer cannot be fetched again by calling anything, so it stays whole for
            // the rest of the session however tight the window gets.
            canItsOutputBeDroppedFromHistory: false);

        // Three is a task that was genuinely underspecified. A fourth is a model using the user as
        // a search engine instead of calling list_dir.
        const int k_maximumQuestionsPerRun = 3;

        public AskUserTool(Func<string, CancellationToken, UniTask<string>> askTheUserAsync)
        {
            _askTheUserAsync = askTheUserAsync;
        }

        public ToolDefinition Definition => k_toolDefinition;

        /// <summary>
        /// Forgets how many questions have been asked. Called at the start of every run, so the
        /// limit binds one task rather than the whole session.
        /// </summary>
        public void ForgetQuestionsAskedInThePreviousRun()
        {
            _questionsAskedInThisRun = 0;
        }

        public async UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            string questionText = toolCall.GetArgument("question");

            if (string.IsNullOrWhiteSpace(questionText))
            {
                return ToolResult.Failure(
                    "ask_user needs a question. Call it again with the one thing you need to know, for example question What should the new name be?");
            }

            if (_askTheUserAsync == null)
            {
                Debug.LogWarning("[AskUserTool] No question gate is wired up, so the model was told nobody is there.");
                return ToolResult.Failure(
                    "ask_user cannot reach anyone right now. Decide for yourself and say what you assumed, or call finish and explain what you needed.");
            }

            var refusalWhenTooManyHaveBeenAsked = BuildRefusalWhenTooManyQuestionsHaveBeenAsked();
            if (refusalWhenTooManyHaveBeenAsked != null)
            {
                return refusalWhenTooManyHaveBeenAsked;
            }

            _questionsAskedInThisRun++;

            // A cancel while the question is open throws OperationCanceledException, and it is
            // deliberately NOT caught here: ToolRunner rethrows it, the run ends as a cancel, and
            // the model never reads an error about the user having pressed Escape.
            string answerText = await _askTheUserAsync(questionText, cancellationToken);

            return BuildResultFromTheAnswer(answerText);
        }

        ToolResult BuildRefusalWhenTooManyQuestionsHaveBeenAsked()
        {
            if (_questionsAskedInThisRun < k_maximumQuestionsPerRun)
            {
                return null;
            }

            return ToolResult.Failure(
                $"you have already asked {k_maximumQuestionsPerRun} questions in this run, which is the limit. " +
                "Work with what you have: look at the project with list_dir, find_file or grep, make a reasonable " +
                "choice and say what you assumed, or call finish and explain what you still needed.");
        }

        // An empty answer is the user pressing Enter on nothing, which is a real thing to do and
        // means "you decide". Told that plainly, the model gets on with it; handed an empty string
        // it re-asks the same question and burns the limit.
        static ToolResult BuildResultFromTheAnswer(string answerText)
        {
            if (string.IsNullOrWhiteSpace(answerText))
            {
                return ToolResult.Success(
                    "The user answered with nothing, which means it is your call. Choose the most reasonable option, " +
                    "carry on, and say what you assumed when you finish.");
            }

            return ToolResult.Success($"The user answered: {answerText.Trim()}");
        }
    }
}
