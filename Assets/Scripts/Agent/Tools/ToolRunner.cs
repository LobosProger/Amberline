using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    // Takes what the parse ladder produced, decides whether it may run, runs it, and hands back a
    // ToolResult. Every refusal and every exception out of an executor comes back as
    // ToolResult.Failure whose text is written for the model to read, because the model is supposed
    // to correct itself and it cannot do that if the loop dies instead.
    //
    // CANCELLATION IS THE ONE EXCEPTION AND IT IS DELIBERATE. The executors call
    // ThrowIfCancellationRequested, so a cancelled tool throws OperationCanceledException. Turning
    // that into a ToolResult.Failure would write "ERROR: the operation was cancelled" into the
    // transcript, and the model would then apologise for the user pressing Escape. So cancellation
    // propagates out of this class untouched and the loop ends the run without recording anything.
    //
    // The refusal helpers below all share one convention: they return null when there is nothing
    // to refuse, and a ToolResult when the call must not run. That keeps RunAsync a flat list of
    // checks in the order they matter.
    //
    // Two of those checks carry real weight:
    //
    // 1. A TRUNCATED MUTATING CALL IS REFUSED. The parser flags a call it only rescued by closing
    //    an unterminated string. For a read that is harmless. For write_file it is not: the repair
    //    turns a cut-off payload into perfectly valid JSON, and writing it would drop half a source
    //    file over a good one. The parser cannot make this call because it does not know which
    //    tools mutate; this class does.
    //
    // 2. MUTATING AND COMMAND CALLS PASS THE APPROVAL GATE FIRST. It arrives as an injected
    //    delegate. A null delegate means "read-only tools only" and every mutating call is refused
    //    with a message saying so. This is now the ONLY thing standing between the model and the
    //    file system - the two-phase tool gate that used to sit in front of it is gone, see
    //    ToolRegistry for what it cost.
    public class ToolRunner
    {
        readonly ToolRegistry _toolRegistry;
        readonly Func<ApprovalRequest, CancellationToken, UniTask<ApprovalDecision>> _requestApprovalAsync;

        // The approval card shows the real diff; this preview is only the payload the model asked
        // for, so a long one is cut rather than pushed through the UI.
        const int k_maximumPreviewLength = 400;

        /// <summary>
        /// Pass null for <paramref name="requestApprovalAsync"/> until the approval gate exists.
        /// Null means read-only tools run and every mutating or command call is refused.
        /// </summary>
        public ToolRunner(ToolRegistry toolRegistry,
            Func<ApprovalRequest, CancellationToken, UniTask<ApprovalDecision>> requestApprovalAsync)
        {
            _toolRegistry = toolRegistry;
            _requestApprovalAsync = requestApprovalAsync;
        }

        /// <summary>
        /// Runs the parsed call, or explains to the model why it did not run. Never returns null.
        /// Throws only <see cref="OperationCanceledException"/>, and only because the user
        /// cancelled - a cancel must never reach the model as a tool error it has to answer for.
        /// </summary>
        public async UniTask<ToolResult> RunAsync(ToolCallParseResult parseResult, CancellationToken cancellationToken)
        {
            if (parseResult == null || !parseResult.IsToolCall || parseResult.Call == null)
            {
                return ToolResult.Failure(BuildMessageForCallThatDidNotParse(parseResult));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var toolCall = parseResult.Call;
            var toolExecutor = _toolRegistry.FindExecutorForToolName(toolCall.ToolName);

            var refusal = BuildRefusalWhenCallMustNotRun(toolCall, toolExecutor, parseResult.WasTruncated);
            if (refusal != null)
            {
                return refusal;
            }

            var toolDefinition = toolExecutor.Definition;

            if (DoesCallNeedApproval(toolDefinition))
            {
                var approvalRefusal = await BuildRefusalWhenApprovalIsNotGrantedAsync(toolCall, toolDefinition, cancellationToken);
                if (approvalRefusal != null)
                {
                    return approvalRefusal;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            return await ExecuteAndTurnToolFailuresIntoResultsAsync(toolExecutor, toolCall, cancellationToken);
        }

        // The parse failure text already says what went wrong; this only makes sure the model is
        // told what to do next, because a bare diagnostic tends to be answered with prose.
        static string BuildMessageForCallThatDidNotParse(ToolCallParseResult parseResult)
        {
            string parseFailureText = parseResult == null || string.IsNullOrEmpty(parseResult.FailureMessage)
                ? "no tool call was found in your reply"
                : parseResult.FailureMessage;

            return $"{parseFailureText}. Reply with exactly one <tool_call>...</tool_call> and nothing else.";
        }

        // Checks in the order the model most needs to hear about. Returns null when the call may run.
        ToolResult BuildRefusalWhenCallMustNotRun(ToolCall toolCall, IToolExecutor toolExecutor, bool wasTruncated)
        {
            var unknownNameRefusal = BuildRefusalWhenToolNameIsUnknown(toolCall);
            if (unknownNameRefusal != null)
            {
                return unknownNameRefusal;
            }

            var missingExecutorRefusal = BuildRefusalWhenExecutorIsMissing(toolCall, toolExecutor);
            if (missingExecutorRefusal != null)
            {
                return missingExecutorRefusal;
            }

            var truncationRefusal = BuildRefusalWhenTruncatedCallWouldChangeSomething(toolExecutor.Definition, wasTruncated);
            if (truncationRefusal != null)
            {
                return truncationRefusal;
            }

            return BuildRefusalWhenArgumentsAreMissing(toolCall, toolExecutor.Definition);
        }

        ToolResult BuildRefusalWhenToolNameIsUnknown(ToolCall toolCall)
        {
            if (_toolRegistry.IsToolNameKnownToRegistry(toolCall.ToolName))
            {
                return null;
            }

            string requestedName = string.IsNullOrEmpty(toolCall.ToolName) ? "(empty)" : toolCall.ToolName;
            return ToolResult.Failure($"there is no tool called '{requestedName}'. Use one of: {DescribeCallableToolNames()}.");
        }

        ToolResult BuildRefusalWhenExecutorIsMissing(ToolCall toolCall, IToolExecutor toolExecutor)
        {
            if (toolExecutor != null && toolExecutor.Definition != null)
            {
                return null;
            }

            return ToolResult.Failure(
                $"{toolCall.ToolName} is not implemented in this build. Use one of: {DescribeCallableToolNames()}.");
        }

        static ToolResult BuildRefusalWhenTruncatedCallWouldChangeSomething(ToolDefinition toolDefinition, bool wasTruncated)
        {
            if (!wasTruncated)
            {
                return null;
            }

            if (!toolDefinition.IsMutating && !toolDefinition.IsCommand)
            {
                return null;
            }

            return ToolResult.Failure(
                $"your {toolDefinition.Name} call was cut off before it ended, so it was not run - running a half " +
                "sent call would change the file in a way you did not ask for. Send the whole call again in one piece.");
        }

        static ToolResult BuildRefusalWhenArgumentsAreMissing(ToolCall toolCall, ToolDefinition toolDefinition)
        {
            var missingParameterNames = new List<string>();

            foreach (string parameterName in toolDefinition.ParameterNames)
            {
                if (toolDefinition.CanRunWithout(parameterName))
                {
                    continue;
                }

                if (!toolCall.Arguments.TryGetValue(parameterName, out var argumentValue) || argumentValue == null)
                {
                    missingParameterNames.Add(parameterName);
                }
            }

            if (missingParameterNames.Count == 0)
            {
                return null;
            }

            string allParameterNames = string.Join(", ", toolDefinition.ParameterNames);
            string missingNames = string.Join(", ", missingParameterNames);

            return ToolResult.Failure(
                $"{toolDefinition.Name} needs all of these arguments: {allParameterNames}. " +
                $"Missing: {missingNames}. Send the call again with every argument filled in.");
        }

        // Shared by the refusals above. Names only the tools that can really run right now, so the
        // model is never pointed at something this build has no executor for.
        string DescribeCallableToolNames()
        {
            var callableNames = _toolRegistry.GetNamesOfCallableTools();
            return callableNames.Count == 0 ? "(no tools are available)" : string.Join(", ", callableNames);
        }

        // Used only when there is no approval gate. Pointing the model at write_file in the same
        // breath as refusing write_file for want of approval would just buy another wasted turn.
        string DescribeToolNamesThatNeedNoApproval()
        {
            var namesThatNeedNoApproval = new List<string>();

            foreach (var toolDefinition in _toolRegistry.GetDefinitionsOfCallableTools())
            {
                if (!DoesCallNeedApproval(toolDefinition))
                {
                    namesThatNeedNoApproval.Add(toolDefinition.Name);
                }
            }

            return namesThatNeedNoApproval.Count == 0 ? "(no tools are available)" : string.Join(", ", namesThatNeedNoApproval);
        }

        static bool DoesCallNeedApproval(ToolDefinition toolDefinition)
        {
            return toolDefinition.IsMutating || toolDefinition.IsCommand;
        }

        // Returns null once the user has approved. The session allow-list and the permission modes
        // live inside the gate, not here - this class only asks and reads the answer.
        async UniTask<ToolResult> BuildRefusalWhenApprovalIsNotGrantedAsync(ToolCall toolCall,
            ToolDefinition toolDefinition, CancellationToken cancellationToken)
        {
            if (_requestApprovalAsync == null)
            {
                return ToolResult.Failure(
                    $"{toolDefinition.Name} needs the user's approval and this build cannot ask for it, " +
                    $"so nothing was changed. You can only use: {DescribeToolNamesThatNeedNoApproval()}.");
            }

            var approvalRequest = BuildApprovalRequestForCall(toolCall, toolDefinition);

            ApprovalDecision approvalDecision;
            try
            {
                approvalDecision = await _requestApprovalAsync(approvalRequest, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The user cancelled the run while the card was on screen. That is not something
                // the model did wrong, so it never learns about it.
                throw;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ToolRunner] The approval gate threw: {exception.GetType().Name}: {exception.Message}");
                return ToolResult.Failure($"the {toolDefinition.Name} call could not be approved, so it was not run.");
            }

            // A null decision is treated as a refusal. Silence must never mean yes for a call that
            // writes to disk or starts a process.
            if (approvalDecision == null || !approvalDecision.IsApproved)
            {
                return BuildRefusalTheModelIsMostLikelyToActOn(approvalDecision, toolCall, toolDefinition);
            }

            return null;
        }

        // Two different noes, and the model has to hear the difference. "The user rejected this"
        // invites another try - measured, three of them in a row. "You already asked and the answer
        // is final" is the one that moves it on to something else.
        static ToolResult BuildRefusalTheModelIsMostLikelyToActOn(ApprovalDecision approvalDecision,
            ToolCall toolCall, ToolDefinition toolDefinition)
        {
            if (approvalDecision == null || !approvalDecision.WasRejectedEarlierInThisRun)
            {
                return ToolResult.RejectedByUser(toolDefinition.Name);
            }

            return ToolResult.RejectedEarlierInThisRun(toolDefinition.Name, DescribeTargetOfCall(toolCall, toolDefinition));
        }

        static ApprovalRequest BuildApprovalRequestForCall(ToolCall toolCall, ToolDefinition toolDefinition)
        {
            return new ApprovalRequest(toolDefinition.Name, DescribeTargetOfCall(toolCall, toolDefinition),
                BuildPreviewTextForCall(toolCall, toolDefinition), toolDefinition.IsCommand, toolCall);
        }

        // The one thing a call is ABOUT: the file for a write, the command line for a command. The
        // gate fingerprints it, the card shows it, and the rejection memory is keyed on it, so all
        // three have to agree on what it is - hence one method rather than three expressions.
        static string DescribeTargetOfCall(ToolCall toolCall, ToolDefinition toolDefinition)
        {
            return toolDefinition.IsCommand ? toolCall.GetArgument("command") : toolCall.GetArgument("path");
        }

        // The payload the model wants applied, nothing cleverer. The real before/after diff is the
        // approval gate's job, because only it may re-read the file at the moment of writing.
        static string BuildPreviewTextForCall(ToolCall toolCall, ToolDefinition toolDefinition)
        {
            var previewText = new StringBuilder();

            foreach (string parameterName in toolDefinition.ParameterNames)
            {
                previewText.Append(parameterName).Append(": ").Append(toolCall.GetArgument(parameterName)).Append('\n');
            }

            if (previewText.Length > k_maximumPreviewLength)
            {
                previewText.Length = k_maximumPreviewLength;
                previewText.Append("\n...");
            }

            return previewText.ToString();
        }

        static async UniTask<ToolResult> ExecuteAndTurnToolFailuresIntoResultsAsync(IToolExecutor toolExecutor,
            ToolCall toolCall, CancellationToken cancellationToken)
        {
            try
            {
                var toolResult = await toolExecutor.ExecuteAsync(toolCall, cancellationToken);
                if (toolResult == null)
                {
                    return ToolResult.Failure($"{toolCall.ToolName} returned nothing. Try a different tool.");
                }

                return toolResult;
            }
            catch (OperationCanceledException)
            {
                // Every executor calls ThrowIfCancellationRequested, so this is the ordinary way a
                // tool ends when the user presses Escape. Rethrow: swallowing it here would put
                // "ERROR: the operation was canceled" in front of the model as if it had failed.
                throw;
            }
            catch (Exception exception)
            {
                // The stack trace goes to the Editor console for us; the model gets the short form,
                // since a stack trace in the transcript is a lot of tokens it cannot act on.
                Debug.LogWarning($"[ToolRunner] {toolCall.ToolName} threw: {exception}");
                return ToolResult.Failure($"{toolCall.ToolName} failed with {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
