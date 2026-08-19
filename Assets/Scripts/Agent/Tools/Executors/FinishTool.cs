using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // finish(summary) - the terminator. It touches nothing: it hands the summary back as a
    // successful result and the loop stops because of WHICH tool was called, never because of what
    // came out of it.
    //
    // A missing summary still finishes. Returning a failure here would push the model back into
    // the loop it has just declared complete, and it would spend its remaining iterations trying to
    // say goodbye properly - so an empty summary becomes a placeholder line instead.
    public class FinishTool : IToolExecutor
    {
        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "finish",
            parameterNames: new[] { "summary" },
            isMutating: false,
            isCommand: false,
            maximumResponseTokens: 512);

        const string k_missingSummaryText = "Finished, but no summary was written.";

        public ToolDefinition Definition => k_toolDefinition;

        public UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            string summaryText = toolCall.GetArgument("summary");

            if (string.IsNullOrWhiteSpace(summaryText))
            {
                return UniTask.FromResult(ToolResult.Success(k_missingSummaryText));
            }

            return UniTask.FromResult(ToolResult.Success(summaryText.Trim()));
        }
    }
}
