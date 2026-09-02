using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    // The ReAct loop: THINK -> ACT -> parse ladder -> execute -> append, over and over until the
    // model calls finish, the round-trip budget runs out, the user cancels, or the backend dies.
    //
    // Five properties of this class are load-bearing:
    //
    // 1. THE FINISHED EVENT FIRES EXACTLY ONCE, from a finally, on every path including a throw and
    //    a cancel. The terminal locks its input row for the length of a run and unlocks it on that
    //    event, so a run that ended silently would leave the terminal dead for the whole session.
    //
    // 2. THE ACT PROMPT IS A BYTE-EXACT PREFIX EXTENSION OF THE THINK PROMPT. Same transcript, same
    //    prefill, plus the thought and the opening tag. That is the only reason llama.cpp's KV cache
    //    survives the second pass of a turn. Nothing here ever rewrites an earlier turn.
    //
    // 3. THE ASSISTANT TURN STORED IS PREFILL + GENERATED TEXT, not a tidied version of it. Storing
    //    anything else would make the NEXT prompt diverge from this one exactly at that turn, and
    //    throw away the cache we just spent two passes filling.
    //
    // 4. THE THINK PASS IS TRIMMED AT THE FIRST <tool_call>. A small model very often runs straight
    //    on from its plan into the call. When it does, and the call parses cleanly, that call is
    //    taken and the ACT pass is skipped entirely - one round trip instead of two.
    //
    // 5. A TOOL FAILURE IS NOT A RUN FAILURE. It goes into the transcript for the model to read and
    //    recover from. Only a dead backend ends the run early, and it ends it at once rather than
    //    spending the remaining budget against something that cannot answer.
    //
    // Collaborators: LlmGateway generates, PromptBuilder shapes the prompts, GbnfGrammarBuilder
    // constrains the ACT pass, ToolCallParser reads the result, ToolRunner runs it, ContextManager
    // stores it, and AgentEvents is the only way anything outside learns what happened.
    public class AgentLoop
    {
        readonly LlmGateway _llmGateway;
        readonly ContextManager _contextManager;
        readonly ToolRegistry _toolRegistry;
        readonly ToolRunner _toolRunner;
        readonly AgentEvents _agentEvents;
        readonly ChatTemplateRenderer _chatTemplateRenderer;
        readonly PromptBuilder _promptBuilder;
        readonly RepeatedCallDetector _repeatedCallDetector = new RepeatedCallDetector();

        int _llmRoundTripsUsedInThisRun;
        bool _hasAutomaticCompactionFailedInThisRun;

        // Separate from the flag above on purpose. Dropping old tool output and summarising are
        // two different attempts at the same problem, and one summary that came back empty must
        // not switch off the half that needs no model call.
        bool _haveOlderToolOutputsAlreadyBeenDroppedInThisRun;
        bool _hasAnyToolSucceededInThisRun;
        bool _wasAnEmptyFinishAlreadyRefusedInThisRun;

        // The cap counts ROUND TRIPS TO THE MODEL, not iterations, so a model that needs both passes
        // every time still terminates in bounded wall-clock time. A clean run costs two per tool
        // call, or one whenever the fast path fires.
        //
        // Raised from 10 in M5, on a measurement rather than a feeling: the accepted verification
        // run - look at the project, build, read the compiler error, edit, build again, finish -
        // spent NINE round trips and only fit because every single call took the fast path. One
        // ACT pass anywhere in it would have ended the run one step short of the answer, which
        // reads to the user as the agent giving up rather than as a budget running out.
        const int k_maximumLlmRoundTripsPerRun = 14;

        // Enough for a plan plus a whole tool call after it, which is what makes the fast path
        // possible. Larger only buys the model more room to ramble before it acts, and every one
        // of these tokens is paid for at generation speed on a local model.
        const int k_maximumTokensForOneThought = 192;

        // The plan is prose and reads better with a little warmth in it. The call is JSON, and a
        // whole file payload inside that JSON: there, anything but the most likely token is a
        // defect. So the ACT pass is greedy, and the two samplers that fight code generation are
        // switched off for it - a repetition penalty tuned for chat quietly pushes the model off
        // the indentation, the closing brace and the "self." that source is made of, and a min-p
        // floor can only remove tokens the grammar had already declared legal.
        const float k_temperatureForOneThought = 0.3f;
        const float k_temperatureForOneToolCall = 0f;
        const int k_mostLikelyTokenOnly = 1;
        const float k_repetitionPenaltyOff = 1f;
        const float k_minimumProbabilityOff = 0f;

        // Used only if the registry somehow offers no callable tool budget to read.
        const int k_fallbackMaximumTokensForOneToolCall = 512;

        // How full the window may get before the transcript is folded into a summary. It has to
        // leave room for the whole of the next turn - the prefill, a plan, a tool call and the tool
        // result that comes back - and a quarter of a 6144-token window is about 1500 tokens, which
        // is roughly two of those. Waiting any longer means the compaction pass itself no longer
        // fits, which is the one failure this exists to prevent.
        const float k_fractionOfTheWindowThatTriggersCompaction = 0.75f;

        // A summary longer than this is not a summary. The five sections PromptBuilder asks for fit
        // in far less; the budget is the ceiling, not the target.
        const int k_maximumTokensForOneSummary = 320;

        const string k_toolCallOpenTag = "<tool_call>";
        const string k_toolCallCloseTag = "</tool_call>";
        const string k_thinkBlockOpenTag = "<think>";
        const string k_thinkBlockCloseTag = "</think>";
        const string k_summaryOfACancelledRun = "cancelled";

        public AgentLoop(LlmGateway llmGateway, ContextManager contextManager, ToolRegistry toolRegistry,
            ToolRunner toolRunner, AgentEvents agentEvents, ChatTemplateRenderer chatTemplateRenderer)
        {
            _llmGateway = llmGateway;
            _contextManager = contextManager;
            _toolRegistry = toolRegistry;
            _toolRunner = toolRunner;
            _agentEvents = agentEvents;
            _chatTemplateRenderer = chatTemplateRenderer;
            _promptBuilder = new PromptBuilder(chatTemplateRenderer);
        }

        /// <summary>
        /// Runs one task from the user to its end and reports how it ended. Never throws - a cancel
        /// and a backend failure both come back as a <see cref="RunReport"/> - and
        /// <see cref="AgentEvents.OnRunFinished"/> is raised exactly once before it returns.
        /// </summary>
        public async UniTask<RunReport> RunAsync(string userTaskText, CancellationToken cancellationToken)
        {
            _llmRoundTripsUsedInThisRun = 0;
            _hasAutomaticCompactionFailedInThisRun = false;
            _haveOlderToolOutputsAlreadyBeenDroppedInThisRun = false;
            _hasAnyToolSucceededInThisRun = false;
            _wasAnEmptyFinishAlreadyRefusedInThisRun = false;
            _repeatedCallDetector.Reset();

            RunReport reportOfThisRun = null;

            try
            {
                _agentEvents.RaiseRunStarted(userTaskText);
                reportOfThisRun = await RunIterationsUntilTheRunEndsAsync(userTaskText, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The user pressed Escape. Nothing about that is written into the transcript, so the
                // model is never asked to apologise for it on the next turn.
                reportOfThisRun = BuildReportForCancelledRun();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                reportOfThisRun = new RunReport(false,
                    $"the run stopped on an unexpected {exception.GetType().Name}: {exception.Message}",
                    _llmRoundTripsUsedInThisRun);
            }
            finally
            {
                if (reportOfThisRun == null)
                {
                    reportOfThisRun = new RunReport(false, "the run ended without reporting why.", _llmRoundTripsUsedInThisRun);
                }

                _agentEvents.RaiseRunFinished(reportOfThisRun);
            }

            return reportOfThisRun;
        }

        /// <summary>
        /// Makes room in the window, in two passes: the bodies of older read-only tool outputs are
        /// dropped for a one-line stand-in, and then whatever is left of the older half is folded
        /// into a single summary. Backs /compact.
        /// </summary>
        /// <remarks>
        /// Dropping comes first because it costs no model call and invents nothing - the content is
        /// gone rather than paraphrased, and the model is told it can call the tool again. Only the
        /// second pass asks the model to write an account of what happened, which is the part worth
        /// avoiding while there is a cheaper option left.
        /// <para>
        /// Returns false only when NEITHER pass changed anything: too little to work with, a model
        /// that would not answer, or a summary that came back empty. A transcript that was left
        /// alone is always better than one replaced by nothing.
        /// </para>
        /// </remarks>
        public async UniTask<bool> CompactTranscriptAsync(CancellationToken cancellationToken)
        {
            // Same order the automatic path uses: drop what can simply be fetched again before
            // asking the model to paraphrase anything. Both halves run, because /compact is the
            // user asking for as much room as can be had rather than for just enough.
            int amountOfTokensFreedByDroppingToolOutput =
                await _contextManager.ReplaceOlderToolOutputsWithTheirShorterTextAsync();

            if (amountOfTokensFreedByDroppingToolOutput > 0)
            {
                _repeatedCallDetector.Reset();

                _agentEvents.RaiseNoticeProduced(
                    $"the older tool output was dropped to free up context, {amountOfTokensFreedByDroppingToolOutput} tokens back");
            }

            bool wasTheOlderPartSummarised = await _contextManager.CompactOlderMessagesAsync(
                olderMessages => SummariseOlderMessagesAsync(olderMessages, cancellationToken));

            return wasTheOlderPartSummarised || amountOfTokensFreedByDroppingToolOutput > 0;
        }

        // The summariser ContextManager is handed. It is an ordinary unconstrained generation over
        // the older messages ALONE - the pinned system prompt is not in front of it, because a
        // summary of what happened does not need the tool spec and the window is what we are trying
        // to save in the first place.
        async UniTask<string> SummariseOlderMessagesAsync(IReadOnlyList<ChatMessage> olderMessages,
            CancellationToken cancellationToken)
        {
            string summarisePrompt = _promptBuilder.BuildSummarisePrompt(olderMessages);

            var samplingForTheSummary = new LlmSamplingOverride
            {
                MaximumTokensToGenerate = k_maximumTokensForOneSummary
            };

            var summaryResult = await _llmGateway.CompleteAsync(
                summarisePrompt,
                onPartialText: null,
                samplingForTheSummary,
                grammar: null,
                stopText: _chatTemplateRenderer.AssistantTurnEndMarker,
                cancellationToken);

            if (!summaryResult.IsSuccess)
            {
                // An empty string is how ContextManager is told to abort without mutating anything,
                // so a failed summarise pass simply leaves the transcript as it was.
                Debug.LogWarning($"[AgentLoop] The transcript could not be summarised: {summaryResult.FailureMessage}");
                return string.Empty;
            }

            return RemoveReasoningAndTurnTagsFromText(summaryResult.Text).Trim();
        }

        async UniTask<RunReport> RunIterationsUntilTheRunEndsAsync(string userTaskText, CancellationToken cancellationToken)
        {
            var reportForARunThatCannotStart = BuildReportWhenTheRunCannotStart(userTaskText);
            if (reportForARunThatCannotStart != null)
            {
                return reportForARunThatCannotStart;
            }

            await _contextManager.AddUserMessageAsync(_promptBuilder.BuildTaskMessageText(userTaskText));

            while (HasBudgetLeftForAnotherModelCall())
            {
                cancellationToken.ThrowIfCancellationRequested();

                await CompactTheTranscriptWhenItIsNearlyTooLongAsync(cancellationToken);

                var reportIfTheRunEndedHere = await RunOneIterationAsync(cancellationToken);
                if (reportIfTheRunEndedHere != null)
                {
                    return reportIfTheRunEndedHere;
                }
            }

            return BuildReportForARunThatRanOutOfBudget();
        }

        RunReport BuildReportWhenTheRunCannotStart(string userTaskText)
        {
            if (_llmGateway == null)
            {
                return new RunReport(false, "no model gateway is wired up, so nothing can run.", 0);
            }

            if (string.IsNullOrWhiteSpace(userTaskText))
            {
                return new RunReport(false, "the message is empty, so there is nothing to do.", 0);
            }

            return null;
        }

        bool HasBudgetLeftForAnotherModelCall()
        {
            return _llmRoundTripsUsedInThisRun < k_maximumLlmRoundTripsPerRun;
        }

        // Compaction is not counted against the round-trip budget: it is housekeeping this class
        // decided to do, not a step the user asked for, and charging the run for it would shorten
        // exactly the long tasks that needed the compaction.
        async UniTask CompactTheTranscriptWhenItIsNearlyTooLongAsync(CancellationToken cancellationToken)
        {
            if (!IsTheTranscriptCloseToFillingTheWindow())
            {
                return;
            }

            // First and cheapest: drop the bodies of old read-only tool outputs. It costs no model
            // call at all, so it is tried before anything is paraphrased - a summary is the model's
            // account of what happened, and the model is the thing least worth trusting here.
            if (await DropOlderToolOutputsAndReportWhetherThatWasEnoughAsync())
            {
                return;
            }

            if (_hasAutomaticCompactionFailedInThisRun)
            {
                return;
            }

            _agentEvents.RaiseNoticeProduced("the conversation is filling the model's window, so the older part is being summarised");

            bool wasTheTranscriptRewritten = await CompactTranscriptAsync(cancellationToken);

            if (wasTheTranscriptRewritten)
            {
                _agentEvents.RaiseNoticeProduced("the older part of the conversation is now one summary");
                return;
            }

            // One attempt per run. A transcript that could not be compacted this iteration will not
            // have become compactable by the next one, and trying again every time would spend the
            // whole run summarising instead of working.
            _hasAutomaticCompactionFailedInThisRun = true;
            _agentEvents.RaiseNoticeProduced("the conversation could not be summarised, so it was left as it is");
        }

        // Returns true when the transcript came back under the threshold on its own, so no summary
        // is needed this iteration.
        async UniTask<bool> DropOlderToolOutputsAndReportWhetherThatWasEnoughAsync()
        {
            if (_haveOlderToolOutputsAlreadyBeenDroppedInThisRun)
            {
                return false;
            }

            int amountOfTokensFreed = await _contextManager.ReplaceOlderToolOutputsWithTheirShorterTextAsync();

            if (amountOfTokensFreed <= 0)
            {
                // Nothing was trimmable this time, and nothing will have become trimmable by the
                // next iteration either: the newest messages are deliberately out of reach and the
                // rest have already been looked at.
                _haveOlderToolOutputsAlreadyBeenDroppedInThisRun = true;
                return false;
            }

            // The run has been told, in the transcript, to call those tools again if it needs what
            // they said. The memory of which calls have already been made would refuse exactly that
            // - "you already have the answer above" is no longer true of anything.
            _repeatedCallDetector.Reset();

            _agentEvents.RaiseNoticeProduced(
                $"the older tool output was dropped to free up context, {amountOfTokensFreed} tokens back");

            return !IsTheTranscriptCloseToFillingTheWindow();
        }

        bool IsTheTranscriptCloseToFillingTheWindow()
        {
            int usableContextTokens = _llmGateway.UsableContextTokens;

            // Zero means the model has not reported its window yet, and a threshold against an
            // unknown ceiling would fire on the first turn of every run.
            if (usableContextTokens <= 0)
            {
                return false;
            }

            return _contextManager.GetTotalTokenCount() >= usableContextTokens * k_fractionOfTheWindowThatTriggersCompaction;
        }

        // Returns null while the run should carry on, and a report the moment it must stop.
        async UniTask<RunReport> RunOneIterationAsync(CancellationToken cancellationToken)
        {
            var thinkResult = await RunThinkPassAsync(cancellationToken);
            if (!thinkResult.IsSuccess)
            {
                return BuildReportForAModelCallThatDidNotAnswer(thinkResult);
            }

            string generatedThinkText = RemoveReasoningAndTurnTagsFromText(thinkResult.Text);
            string thoughtText = TakeTextBeforeTheFirstToolCall(generatedThinkText).TrimEnd();
            _agentEvents.RaiseThoughtProduced(thoughtText.Trim());

            var parseResult = TryTakeTheToolCallTheThinkPassRanInto(generatedThinkText);
            string assistantTurnText;

            if (parseResult != null)
            {
                // The fast path: the model wrote its plan and the call in one breath, so the whole
                // ACT pass is skipped and this turn costs one round trip instead of two.
                assistantTurnText = _promptBuilder.ThoughtPrefill + TakeTextUpToTheEndOfTheFirstToolCall(generatedThinkText);
            }
            else
            {
                if (!HasBudgetLeftForAnotherModelCall())
                {
                    return BuildReportForARunThatRanOutOfBudget();
                }

                var actOutcome = await RunActPassAsync(thoughtText, cancellationToken);
                if (!actOutcome.Result.IsSuccess)
                {
                    return BuildReportForAModelCallThatDidNotAnswer(actOutcome.Result);
                }

                string generatedCallText = RemoveReasoningAndTurnTagsFromText(actOutcome.Result.Text);
                parseResult = ToolCallParser.Parse(PromptBuilder.k_toolCallPrefill + generatedCallText);
                assistantTurnText = actOutcome.Prefill + generatedCallText;
            }

            await _contextManager.AddAssistantMessageAsync(assistantTurnText);

            var toolResult = await ExecuteParsedCallAsync(parseResult, cancellationToken);

            if (DidTheModelCallFinish(parseResult, toolResult))
            {
                return new RunReport(true, toolResult.Output, _llmRoundTripsUsedInThisRun);
            }

            await AppendToolResultToTranscriptAsync(parseResult, toolResult);
            return null;
        }

        // Unconstrained on purpose: the model plans in plain text here, and the plan is what makes
        // the constrained call that follows a sensible one instead of the first tool it thought of.
        async UniTask<LlmCompletionResult> RunThinkPassAsync(CancellationToken cancellationToken)
        {
            string thinkPrompt = _promptBuilder.BuildThinkPrompt(_contextManager.Messages);

            var samplingForTheThought = new LlmSamplingOverride
            {
                MaximumTokensToGenerate = k_maximumTokensForOneThought,
                Temperature = k_temperatureForOneThought
            };

            _llmRoundTripsUsedInThisRun++;

            return await _llmGateway.CompleteAsync(
                thinkPrompt,
                rawCumulativeText => ShowThoughtWhileItIsBeingWritten(rawCumulativeText),
                samplingForTheThought,
                grammar: null,
                stopText: _chatTemplateRenderer.AssistantTurnEndMarker,
                cancellationToken);
        }

        // The raw text carries the ChatML tags and may already have run into the tool call, and
        // neither belongs on screen, so it is cleaned on the way through.
        void ShowThoughtWhileItIsBeingWritten(string rawCumulativeText)
        {
            string cleanedText = RemoveReasoningAndTurnTagsFromText(rawCumulativeText);
            _agentEvents.RaiseAnswerTextStreaming(TakeTextBeforeTheFirstToolCall(cleanedText).Trim());
        }

        /// <summary>What the ACT pass produced, together with the prefill it ran on - the loop needs
        /// both, because the assistant turn it stores is prefill plus generated text.</summary>
        readonly struct ActPassOutcome
        {
            public LlmCompletionResult Result { get; }
            public string Prefill { get; }

            public ActPassOutcome(LlmCompletionResult result, string prefill)
            {
                Result = result;
                Prefill = prefill;
            }
        }

        // Grammar-constrained, so a malformed call is not something the model is able to produce.
        // The grammar and the prefill are built by one call and used together: the prefill already
        // emitted the opening tag, and a grammar only constrains GENERATED tokens, so a grammar that
        // opened with the tag as well would force the model to write it twice.
        async UniTask<ActPassOutcome> RunActPassAsync(string thoughtText, CancellationToken cancellationToken)
        {
            var toolCallGrammar = GbnfGrammarBuilder.BuildGrammarForTools(_toolRegistry.GetDefinitionsOfCallableTools());

            string actPrefill = _promptBuilder.BuildActPrefill(thoughtText, null, toolCallGrammar.PrefillText);
            string actPrompt = _promptBuilder.BuildActPrompt(_contextManager.Messages, thoughtText, null, toolCallGrammar.PrefillText);

            var samplingForTheCall = new LlmSamplingOverride
            {
                MaximumTokensToGenerate = FindLargestResponseBudgetAmongCallableTools(),
                Temperature = k_temperatureForOneToolCall,
                TopK = k_mostLikelyTokenOnly,
                RepeatPenalty = k_repetitionPenaltyOff,
                MinP = k_minimumProbabilityOff
            };

            _llmRoundTripsUsedInThisRun++;

            var actResult = await _llmGateway.CompleteAsync(
                actPrompt,
                onPartialText: null,
                samplingForTheCall,
                toolCallGrammar.GrammarText,
                stopText: k_toolCallCloseTag,
                cancellationToken);

            return new ActPassOutcome(actResult, actPrefill);
        }

        // The budgets are per tool - a whole-file write needs far more room than a read - but the
        // pass that has to fit inside one does not yet know which tool the model will pick. So the
        // largest budget on offer this turn is the only one that cannot cut a legal call in half.
        int FindLargestResponseBudgetAmongCallableTools()
        {
            int largestBudget = 0;

            foreach (var toolDefinition in _toolRegistry.GetDefinitionsOfCallableTools())
            {
                if (toolDefinition.MaximumResponseTokens > largestBudget)
                {
                    largestBudget = toolDefinition.MaximumResponseTokens;
                }
            }

            return largestBudget > 0 ? largestBudget : k_fallbackMaximumTokensForOneToolCall;
        }

        async UniTask<ToolResult> ExecuteParsedCallAsync(ToolCallParseResult parseResult, CancellationToken cancellationToken)
        {
            ToolCall toolCall = parseResult != null && parseResult.IsToolCall ? parseResult.Call : null;

            if (toolCall != null)
            {
                _agentEvents.RaiseToolCallStarting(toolCall, parseResult.Stage);
            }

            var toolResult = await RunTheCallOrRefuseARepeatAsync(parseResult, toolCall, cancellationToken);

            if (toolCall != null)
            {
                _agentEvents.RaiseToolResultProduced(toolCall, toolResult);
            }

            return toolResult;
        }

        async UniTask<ToolResult> RunTheCallOrRefuseARepeatAsync(ToolCallParseResult parseResult, ToolCall toolCall,
            CancellationToken cancellationToken)
        {
            bool isTheSameCallAsLastTime = toolCall != null && _repeatedCallDetector.IsRepeatOfPreviousCall(toolCall);
            _repeatedCallDetector.RememberCall(toolCall);

            if (isTheSameCallAsLastTime)
            {
                return BuildRefusalForARepeatedCall(toolCall);
            }

            if (toolCall != null && _repeatedCallDetector.HasThisExactCallAlreadyFailed(toolCall))
            {
                return BuildRefusalForACallThatAlreadyFailed(toolCall);
            }

            var refusalOfAnEmptyFinish = BuildRefusalWhenFinishWouldEndARunThatDidNothing(toolCall);
            if (refusalOfAnEmptyFinish != null)
            {
                return refusalOfAnEmptyFinish;
            }

            var toolResult = await _toolRunner.RunAsync(parseResult, cancellationToken);

            if (!toolResult.IsSuccess)
            {
                _repeatedCallDetector.RememberCallThatFailed(toolCall);
            }
            else if (DidThisCallChangeSomethingOutsideTheProcess(toolCall))
            {
                // The world just moved, so every call that failed against the old state deserves
                // another go - starting with the command the agent is about to re-run to check
                // the fix it has only this moment written.
                _repeatedCallDetector.ForgetCallsThatFailed();
            }

            RememberWhetherThisCallDidAnyWork(toolCall, toolResult);
            return toolResult;
        }

        bool DidThisCallChangeSomethingOutsideTheProcess(ToolCall toolCall)
        {
            var toolExecutor = toolCall == null ? null : _toolRegistry.FindExecutorForToolName(toolCall.ToolName);
            if (toolExecutor == null || toolExecutor.Definition == null)
            {
                return false;
            }

            return toolExecutor.Definition.IsMutating || toolExecutor.Definition.IsCommand;
        }

        // The same call, unchanged, after it has already failed once. It will fail the same way, so
        // the round trip is spent on the way out of the loop instead of on the loop.
        static ToolResult BuildRefusalForACallThatAlreadyFailed(ToolCall toolCall)
        {
            return ToolResult.Failure(
                $"you already sent this exact {toolCall.ToolName} call in this run and it failed. Sending it again " +
                "changes nothing. Change the arguments, use a different tool - write_file replaces a whole file and " +
                "needs no anchor - or call finish and say what stopped you.");
        }

        // finish is the one tool that ends the run, and a model that calls it first ends the run
        // having done nothing at all - with a summary that reads like a report of work it never
        // did. Measured twice in one session: "Wrote hello.py." for a file that was never created,
        // and "Reading hello.py to confirm changes." as the final answer of a run that read
        // nothing. The user is told the task is done; the disk says otherwise.
        //
        // Refused ONCE per run, never twice. A run really can be over after nothing - "thanks,
        // that is all" - and a guard that could not be talked past would trap the model in a loop
        // it has no way out of. One nudge is enough to get the work started and cheap enough to be
        // wrong about.
        ToolResult BuildRefusalWhenFinishWouldEndARunThatDidNothing(ToolCall toolCall)
        {
            if (toolCall == null || toolCall.ToolName != ToolRegistry.k_finishToolName)
            {
                return null;
            }

            if (_hasAnyToolSucceededInThisRun || _wasAnEmptyFinishAlreadyRefusedInThisRun)
            {
                return null;
            }

            _wasAnEmptyFinishAlreadyRefusedInThisRun = true;

            return ToolResult.Failure(
                "you have not used a single tool yet, so there is nothing to finish and nothing to report. " +
                "Do the work first: list_dir or read_file to look, write_file or edit_file to change something, " +
                "run_command to check it. Call finish once the work is actually on disk.");
        }

        void RememberWhetherThisCallDidAnyWork(ToolCall toolCall, ToolResult toolResult)
        {
            if (toolCall == null || toolResult == null || !toolResult.IsSuccess)
            {
                return;
            }

            if (toolCall.ToolName == ToolRegistry.k_finishToolName)
            {
                return;
            }

            _hasAnyToolSucceededInThisRun = true;
        }

        // Told plainly and handed back, rather than ending the run. A model stuck on one call can
        // usually get itself out with a nudge, and killing the run would look like a crash.
        static ToolResult BuildRefusalForARepeatedCall(ToolCall toolCall)
        {
            return ToolResult.Failure(
                $"you already called {toolCall.ToolName} with exactly these arguments and you have the answer above. " +
                "Do not send it again. Use a different tool or different arguments, or call finish with what you know.");
        }

        static bool DidTheModelCallFinish(ToolCallParseResult parseResult, ToolResult toolResult)
        {
            if (parseResult == null || !parseResult.IsToolCall || parseResult.Call == null)
            {
                return false;
            }

            return parseResult.Call.ToolName == ToolRegistry.k_finishToolName && toolResult.IsSuccess;
        }

        // Decorated HERE, at insert time, and never at render time. The text that goes in is the
        // text that is token-counted and the text every later prompt reproduces byte for byte -
        // decorating on the way out would quietly break the append-only guarantee the cache rests on.
        async UniTask AppendToolResultToTranscriptAsync(ToolCallParseResult parseResult, ToolResult toolResult)
        {
            await _contextManager.AddUserMessageAsync(
                _promptBuilder.BuildToolResultMessageText(toolResult.Output),
                BuildShorterTextThisResultCanBeCutDownToLater(parseResult, toolResult));
        }

        // Worked out now, while the call that produced this output is still in hand, and carried
        // along with the message. By the time the window fills up, nothing in the transcript says
        // which tool wrote which result - the text between the tags is the output and nothing else.
        //
        // Null for everything that has to be kept whole: a failure the model still has to read, and
        // any tool whose output cannot simply be fetched again by calling it a second time.
        string BuildShorterTextThisResultCanBeCutDownToLater(ToolCallParseResult parseResult, ToolResult toolResult)
        {
            if (!toolResult.IsSuccess) return null;
            if (parseResult == null || !parseResult.IsToolCall || parseResult.Call == null) return null;

            var toolExecutor = _toolRegistry.FindExecutorForToolName(parseResult.Call.ToolName);
            if (toolExecutor?.Definition == null) return null;
            if (!toolExecutor.Definition.CanItsOutputBeDroppedFromHistory) return null;

            return _promptBuilder.BuildTrimmedToolResultMessageText(parseResult.Call);
        }

        // The model wrote its plan and kept going into the call. Taking that call is worth a whole
        // round trip, but only when it is complete: a call the token budget cut in half is handed
        // back to the ACT pass instead, where the grammar can write it properly.
        //
        // ONLY THE FIRST WHOLE BLOCK IS READ, and that is not fussiness. A model that keeps
        // narrating often writes a second call after the first - measured, a correct edit_file
        // followed by a speculative finish - and the parse ladder takes the LAST block it is given,
        // while the assistant turn stored below keeps only up to the FIRST closing tag. Handing the
        // ladder the whole tail therefore executed one call and wrote a different one into the
        // transcript, so the next turn read a history that had never happened.
        static ToolCallParseResult TryTakeTheToolCallTheThinkPassRanInto(string generatedThinkText)
        {
            string firstWholeToolCallBlock = TakeTheFirstWholeToolCallBlock(generatedThinkText);
            if (firstWholeToolCallBlock == null)
            {
                return null;
            }

            var parseResult = ToolCallParser.Parse(firstWholeToolCallBlock);

            if (!parseResult.IsToolCall || parseResult.WasTruncated)
            {
                return null;
            }

            return parseResult;
        }

        // Null when there is no call, or when the one that started never finished - both are cases
        // for the ACT pass rather than for guesswork here.
        static string TakeTheFirstWholeToolCallBlock(string generatedText)
        {
            int indexOfOpenTag = generatedText.IndexOf(k_toolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (indexOfOpenTag < 0)
            {
                return null;
            }

            int indexOfCloseTag = generatedText.IndexOf(k_toolCallCloseTag, indexOfOpenTag, StringComparison.OrdinalIgnoreCase);
            if (indexOfCloseTag < 0)
            {
                return null;
            }

            return generatedText.Substring(indexOfOpenTag, indexOfCloseTag + k_toolCallCloseTag.Length - indexOfOpenTag);
        }

        static string TakeTextBeforeTheFirstToolCall(string generatedText)
        {
            int indexOfOpenTag = generatedText.IndexOf(k_toolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            return indexOfOpenTag < 0 ? generatedText : generatedText.Substring(0, indexOfOpenTag);
        }

        static string TakeTextUpToTheEndOfTheFirstToolCall(string generatedText)
        {
            int indexOfOpenTag = generatedText.IndexOf(k_toolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (indexOfOpenTag < 0)
            {
                return generatedText;
            }

            int indexOfCloseTag = generatedText.IndexOf(k_toolCallCloseTag, indexOfOpenTag, StringComparison.OrdinalIgnoreCase);
            if (indexOfCloseTag < 0)
            {
                return generatedText;
            }

            return generatedText.Substring(0, indexOfCloseTag + k_toolCallCloseTag.Length);
        }

        // Kept from M2 as belt and braces beside the closed think block the prefill already opens
        // with. That prefill is a prompt-level trick, not a guarantee, and a small model can still
        // open a block of its own.
        string RemoveReasoningAndTurnTagsFromText(string generatedText)
        {
            if (string.IsNullOrEmpty(generatedText))
            {
                return string.Empty;
            }

            // The escapes come off FIRST, before anything looks for a tag or a brace. Every reader
            // below this line searches for literal "<tool_call>" and literal JSON, and a model that
            // escaped its underscores hands them "<tool\_call>" instead - which matches nothing at
            // all. See MarkdownEscapeCleaner for the run this cost.
            string textWithoutMarkdownEscapes = MarkdownEscapeCleaner.RemoveMarkdownEscapesFromText(generatedText);

            string textWithoutReasoning = RemoveThinkBlocksFromText(textWithoutMarkdownEscapes);
            return CutTextAtTheEndOfTheAssistantTurn(textWithoutReasoning).TrimEnd();
        }

        // A CLOSED block is reasoning the model has already finished with, and what follows it is
        // its conclusion - so the block goes and the conclusion stays.
        //
        // An UNCLOSED one used to take everything with it, and that was a real bug rather than a
        // tidy-up: a reasoning model that spends its whole thought budget inside one block has
        // written a perfectly good plan and never got to close it, and returning the empty string
        // for it threw the plan away, showed the user a spinner over nothing, and sent the ACT
        // pass in blind. Now only the tag is dropped and the reasoning becomes the thought, which
        // is exactly what the folded "thinking" line in the terminal is for.
        static string RemoveThinkBlocksFromText(string generatedText)
        {
            string remainingText = generatedText;

            while (true)
            {
                int openTagIndex = remainingText.IndexOf(k_thinkBlockOpenTag, StringComparison.Ordinal);
                if (openTagIndex < 0)
                {
                    return remainingText;
                }

                int closeTagIndex = remainingText.IndexOf(k_thinkBlockCloseTag, openTagIndex, StringComparison.Ordinal);
                if (closeTagIndex < 0)
                {
                    return remainingText.Remove(openTagIndex, k_thinkBlockOpenTag.Length);
                }

                int lengthOfTheWholeBlock = closeTagIndex + k_thinkBlockCloseTag.Length - openTagIndex;
                remainingText = remainingText.Remove(openTagIndex, lengthOfTheWholeBlock);
            }
        }

        // Cut, never erase. The gateway trims the FINAL text at the stop marker, but the streaming
        // callbacks carry the raw text, so a model running past the marker would otherwise flash the
        // opening of a turn it invented on screen before the cancel lands.
        //
        // Which markers those are is the loaded model's business, not ours - see
        // ChatTemplateRenderer. Hard-coding the ChatML pair here was one of the places where a
        // Mistral ran straight on into writing the user's next message and nothing stopped it.
        string CutTextAtTheEndOfTheAssistantTurn(string generatedText)
        {
            int indexWhereTheTurnEnds = _chatTemplateRenderer.FindIndexWhereTheAssistantTurnEnds(generatedText);
            return generatedText.Substring(0, indexWhereTheTurnEnds);
        }

        RunReport BuildReportForCancelledRun()
        {
            return new RunReport(false, k_summaryOfACancelledRun, _llmRoundTripsUsedInThisRun, true);
        }

        // A dead backend ends the run at once. Retrying against it would only burn the whole budget
        // and make the terminal look hung for a minute before it said the same thing.
        RunReport BuildReportForAModelCallThatDidNotAnswer(LlmCompletionResult completionResult)
        {
            if (completionResult.WasCancelled)
            {
                return BuildReportForCancelledRun();
            }

            return new RunReport(false, $"the model stopped answering: {completionResult.FailureMessage}", _llmRoundTripsUsedInThisRun);
        }

        RunReport BuildReportForARunThatRanOutOfBudget()
        {
            return new RunReport(false,
                $"stopped after {_llmRoundTripsUsedInThisRun} model calls without reaching an answer. " +
                "Ask again with a narrower question, or point at a specific file.",
                _llmRoundTripsUsedInThisRun);
        }
    }
}
