using System;
using System.Collections.Generic;

namespace Amberline.Agent
{
    /// <summary>
    /// Who produced a message in the transcript. Deliberately smaller than the OpenAI role set:
    /// this agent has no separate tool role, because tool results are fed back as user turns.
    /// </summary>
    public enum ChatRole
    {
        System,
        User,
        Assistant
    }

    /// <summary>
    /// One turn in the transcript. The token count is measured once, when the message is added,
    /// rather than recomputed on every context check - counting is a native call and the text
    /// never changes after insertion.
    /// </summary>
    public class ChatMessage
    {
        public ChatRole Role { get; }
        public string Text { get; }
        public int TokenCount { get; }

        public ChatMessage(ChatRole role, string text, int tokenCount)
        {
            Role = role;
            Text = text ?? string.Empty;
            TokenCount = tokenCount;
        }
    }

    /// <summary>
    /// How full the model's context window currently is. Rendered in the status bar so the user
    /// can see the budget running out before the agent starts forgetting things.
    /// </summary>
    public readonly struct ContextUsage
    {
        public int UsedTokens { get; }
        public int MaxTokens { get; }

        public ContextUsage(int usedTokens, int maxTokens)
        {
            UsedTokens = usedTokens;
            MaxTokens = maxTokens;
        }

        /// <summary>Fraction of the window in use, clamped to 0..1. Zero when the max is unknown.</summary>
        public float FillRatio => MaxTokens <= 0 ? 0f : Math.Min(1f, (float)UsedTokens / MaxTokens);
    }

    /// <summary>
    /// Outcome of one call to the model. Failure and cancellation are values rather than
    /// exceptions, because both are ordinary things for an agent turn to end with and the loop
    /// has to keep running afterwards either way.
    /// </summary>
    public class LlmCompletionResult
    {
        public bool IsSuccess { get; }
        public bool WasCancelled { get; }
        public string Text { get; }
        public string FailureMessage { get; }

        LlmCompletionResult(bool isSuccess, bool wasCancelled, string text, string failureMessage)
        {
            IsSuccess = isSuccess;
            WasCancelled = wasCancelled;
            Text = text ?? string.Empty;
            FailureMessage = failureMessage ?? string.Empty;
        }

        public static LlmCompletionResult Success(string text)
        {
            return new LlmCompletionResult(true, false, text, null);
        }

        public static LlmCompletionResult Cancelled(string partialText)
        {
            return new LlmCompletionResult(false, true, partialText, "cancelled");
        }

        public static LlmCompletionResult Failure(string failureMessage)
        {
            return new LlmCompletionResult(false, false, null, failureMessage);
        }
    }

    /// <summary>
    /// How far down the parse ladder the terminal had to go to get a usable tool call out of the
    /// model's text. Recorded so a run that only worked because of aggressive repair is visible
    /// rather than silently indistinguishable from a clean one.
    /// </summary>
    public enum ToolCallParseStage
    {
        /// <summary>Nothing usable was found.</summary>
        Failed,

        /// <summary>The model emitted a well-formed call and it parsed as-is.</summary>
        DirectExtraction,

        /// <summary>The JSON needed repairing before it would parse.</summary>
        JsonRepair,

        /// <summary>Argument values had to be coerced into the shapes the tool expects.</summary>
        Coercion
    }

    /// <summary>
    /// One tool invocation the model asked for. Arguments are strings because that is what comes
    /// off the wire; each executor is responsible for interpreting its own.
    /// </summary>
    public class ToolCall
    {
        public string ToolName { get; }
        public IReadOnlyDictionary<string, string> Arguments { get; }

        public ToolCall(string toolName, IReadOnlyDictionary<string, string> arguments)
        {
            ToolName = toolName ?? string.Empty;
            Arguments = arguments ?? new Dictionary<string, string>();
        }

        /// <summary>Returns the argument, or <paramref name="fallbackValue"/> when it is absent.</summary>
        public string GetArgument(string parameterName, string fallbackValue = "")
        {
            return Arguments.TryGetValue(parameterName, out var value) ? value : fallbackValue;
        }
    }

    /// <summary>
    /// What came back from the parse ladder. A truncated call is flagged rather than rejected here,
    /// because only the runner knows whether the tool it names is a mutating one - and a truncated
    /// mutating call must never execute, since repair would happily complete a cut-off write.
    /// </summary>
    public class ToolCallParseResult
    {
        public bool IsToolCall { get; }
        public ToolCall Call { get; }
        public ToolCallParseStage Stage { get; }
        public bool WasTruncated { get; }
        public string FailureMessage { get; }

        ToolCallParseResult(bool isToolCall, ToolCall call, ToolCallParseStage stage, bool wasTruncated, string failureMessage)
        {
            IsToolCall = isToolCall;
            Call = call;
            Stage = stage;
            WasTruncated = wasTruncated;
            FailureMessage = failureMessage ?? string.Empty;
        }

        public static ToolCallParseResult Parsed(ToolCall call, ToolCallParseStage stage, bool wasTruncated)
        {
            return new ToolCallParseResult(true, call, stage, wasTruncated, null);
        }

        public static ToolCallParseResult NotAToolCall(string failureMessage)
        {
            return new ToolCallParseResult(false, null, ToolCallParseStage.Failed, false, failureMessage);
        }
    }

    /// <summary>
    /// What a tool produced. Failures are values, not exceptions: the model is supposed to read the
    /// error and correct itself, which it cannot do if the loop dies instead.
    /// </summary>
    public class ToolResult
    {
        public bool IsSuccess { get; }
        public string Output { get; }
        public bool WasRejectedByUser { get; }

        /// <summary>Workspace-relative path of the file this call changed. Empty when it changed none.</summary>
        public string ChangedFileDisplayPath { get; }

        /// <summary>
        /// The change that actually landed on disk, so the terminal can show the same diff the
        /// approval card would have shown - including in the modes where no card is ever drawn.
        /// Null for every tool that does not write. This is for the USER: it never reaches the
        /// model, which only ever sees <see cref="Output"/>.
        /// </summary>
        public FileDiff AppliedFileDiff { get; }

        ToolResult(bool isSuccess, string output, bool wasRejectedByUser,
            string changedFileDisplayPath = null, FileDiff appliedFileDiff = null)
        {
            IsSuccess = isSuccess;
            Output = output ?? string.Empty;
            WasRejectedByUser = wasRejectedByUser;
            ChangedFileDisplayPath = changedFileDisplayPath ?? string.Empty;
            AppliedFileDiff = appliedFileDiff;
        }

        public static ToolResult Success(string output)
        {
            return new ToolResult(true, output, false);
        }

        /// <summary>
        /// A success that also wrote a file. Carries the diff alongside the text so the terminal
        /// can render what changed; <paramref name="output"/> is unchanged from what the plain
        /// <see cref="Success"/> would have produced, so the transcript the model reads - and the
        /// prompt prefix the KV cache is built on - stays byte for byte the same.
        /// </summary>
        public static ToolResult SuccessWithFileChange(string output, string changedFileDisplayPath, FileDiff appliedFileDiff)
        {
            return new ToolResult(true, output, false, changedFileDisplayPath, appliedFileDiff);
        }

        /// <summary>The message is written for the model to read, so it must say what to do next.</summary>
        public static ToolResult Failure(string errorMessage)
        {
            return new ToolResult(false, "ERROR: " + errorMessage, false);
        }

        public static ToolResult RejectedByUser(string toolName)
        {
            return new ToolResult(false, $"ERROR: the user rejected the {toolName} call. Do not repeat it.", true);
        }

        /// <summary>
        /// The user already rejected this exact target earlier and was NOT asked again. Measured on
        /// Qwen3-8B: told only that a call was rejected, it re-sent the same one three times in a
        /// row while narrating that it would try a different approach. So this message does not
        /// merely forbid the call - it says the question is closed and names the two moves left.
        /// </summary>
        public static ToolResult RejectedEarlierInThisRun(string toolName, string targetDescription)
        {
            string targetText = string.IsNullOrEmpty(targetDescription) ? string.Empty : $" on {targetDescription}";

            return new ToolResult(false,
                $"ERROR: you already proposed {toolName}{targetText} and the user rejected it, so it was not run and " +
                "the user was not asked a second time. That decision is final for this task. Do something different, " +
                "or call finish and say what is left undone.", true);
        }
    }

    /// <summary>
    /// How much the user wants to be asked. Commands are always asked about, in every mode.
    /// </summary>
    public enum PermissionMode
    {
        /// <summary>Ask before every write and every command.</summary>
        AskEveryTime,

        /// <summary>Writes inside the workspace go through; commands still ask.</summary>
        AutoApproveEdits
    }

    /// <summary>One pending approval, handed to the UI and answered by the user.</summary>
    public class ApprovalRequest
    {
        public string ToolName { get; }
        public string TargetDescription { get; }
        public string PreviewText { get; }
        public bool IsCommand { get; }

        /// <summary>
        /// The exact call waiting on this answer. Carried so the front end can ask the tool's own
        /// executor what the call would do and show that, rather than reconstructing it from
        /// <see cref="PreviewText"/> - which is a truncated display string and cannot be parsed
        /// back. Never null for a request built by ToolRunner.
        /// </summary>
        public ToolCall Call { get; }

        public ApprovalRequest(string toolName, string targetDescription, string previewText, bool isCommand, ToolCall call)
        {
            ToolName = toolName ?? string.Empty;
            TargetDescription = targetDescription ?? string.Empty;
            PreviewText = previewText ?? string.Empty;
            IsCommand = isCommand;
            Call = call;
        }
    }

    /// <summary>The user's answer to an <see cref="ApprovalRequest"/>.</summary>
    public class ApprovalDecision
    {
        public bool IsApproved { get; }
        public bool ShouldRememberForSession { get; }

        /// <summary>
        /// True when the gate refused without drawing a card at all, because the user had already
        /// rejected this tool and target earlier in the run. The runner reads it to tell the model
        /// the question is closed, rather than repeating the one sentence it has proved it ignores.
        /// </summary>
        public bool WasRejectedEarlierInThisRun { get; }

        public ApprovalDecision(bool isApproved, bool shouldRememberForSession, bool wasRejectedEarlierInThisRun = false)
        {
            IsApproved = isApproved;
            ShouldRememberForSession = shouldRememberForSession;
            WasRejectedEarlierInThisRun = wasRejectedEarlierInThisRun;
        }
    }

    /// <summary>
    /// How a whole agent run ended. Rendered as the closing line of a turn.
    /// <para>
    /// A cancelled run is kept apart from a failed one because the terminal renders them
    /// differently: the user pressing Escape is a notice, a dead backend is an error.
    /// </para>
    /// </summary>
    public class RunReport
    {
        public bool DidFinishCleanly { get; }
        public string SummaryText { get; }

        /// <summary>How many round trips to the model the run spent, against the per-run cap.</summary>
        public int IterationsUsed { get; }

        /// <summary>True when the user stopped the run. Never true together with DidFinishCleanly.</summary>
        public bool WasCancelled { get; }

        public RunReport(bool didFinishCleanly, string summaryText, int iterationsUsed, bool wasCancelled = false)
        {
            DidFinishCleanly = didFinishCleanly;
            SummaryText = summaryText ?? string.Empty;
            IterationsUsed = iterationsUsed;
            WasCancelled = wasCancelled;
        }
    }
}
