using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // run_command(command) - the generic verification loop. This is what lets the agent check its
    // own work on any project in any language: `dotnet build`, `npm run build`, `pytest`. Nothing
    // in here knows what Unity is, and that is deliberate - Unity is only the host.
    //
    // Three decisions:
    //
    // 1. IT IS APPROVAL-GATED IN EVERY PERMISSION MODE. isCommand is true, and ApprovalGate refuses
    //    to consult either the permission mode or the session allow-list for a command. A command
    //    is the only thing the agent can ask for that reaches outside the workspace entirely, so
    //    this is a fixed product decision rather than a default somebody may flip later.
    //
    // 2. IT IS ALSO MARKED MUTATING. A command changes things, and ToolRunner refuses a TRUNCATED
    //    mutating call rather than running it. Half of `rm -rf build` is a different command from
    //    the whole of it, and repair would happily complete the JSON around it.
    //
    // 3. THE MODEL GETS THE TAIL, NOT THE HEAD. A build prints its errors last - a hundred lines of
    //    restore chatter first, then the three lines that matter - so the last 60 lines are what go
    //    back into the transcript. That is the one place in this product where the tail-biased
    //    budget in ToolOutputTruncator is used, and it is why that method exists.
    public class RunCommandTool : IToolExecutor
    {
        readonly CommandRunner _commandRunner;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "run_command",
            parameterNames: new[] { "command" },
            isMutating: true,
            isCommand: true,
            maximumResponseTokens: 512);

        const string k_hintAfterAFailedCommand =
            "The command failed. Read the output above, change what caused it, then run the same command again to check.";

        const string k_hintAboutTheTruncatedHead =
            "Only the end of the output is shown, because that is where a build puts its errors.";

        public RunCommandTool(CommandRunner commandRunner)
        {
            _commandRunner = commandRunner;
        }

        public ToolDefinition Definition => k_toolDefinition;

        public async UniTask<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken)
        {
            string commandText = toolCall.GetArgument("command").Trim();

            if (string.IsNullOrEmpty(commandText))
            {
                return ToolResult.Failure(
                    "run_command needs a command. Call it again with the exact line you would type, for example command dotnet build.");
            }

            if (_commandRunner == null)
            {
                return ToolResult.Failure("run_command is not wired up in this build, so no command can be run.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var commandRunResult = await _commandRunner.RunAsync(commandText, cancellationToken);
            return BuildToolResultForCommandRun(commandText, commandRunResult);
        }

        static ToolResult BuildToolResultForCommandRun(string commandText, CommandRunResult commandRunResult)
        {
            if (!commandRunResult.DidStart)
            {
                return ToolResult.Failure(
                    $"the command could not be started: {commandRunResult.StartFailureMessage}. Check the command name and try a different one.");
            }

            string outputTail = BuildOutputTailWithTruncationNote(commandRunResult);

            if (commandRunResult.DidTimeOut)
            {
                return ToolResult.Failure(
                    $"{commandText} was still running after {CommandRunner.k_maximumCommandSeconds} seconds, so it was stopped.\n" +
                    $"{outputTail}\nRun something faster, or narrow what the command does.");
            }

            if (commandRunResult.ExitCode == 0)
            {
                return ToolResult.Success($"exit code 0 - {commandText} succeeded.\n{outputTail}");
            }

            return ToolResult.Failure($"exit code {commandRunResult.ExitCode} from: {commandText}\n{outputTail}\n{k_hintAfterAFailedCommand}");
        }

        // The tail, plus an honest note whenever the head was dropped. A model handed a silent
        // extract believes it has seen the whole log and reports the wrong error with confidence.
        static string BuildOutputTailWithTruncationNote(CommandRunResult commandRunResult)
        {
            if (commandRunResult.OutputLines.Count == 0)
            {
                return "(the command printed nothing)";
            }

            string outputTail = ToolOutputTruncator.JoinLastLinesWithinBudget(
                commandRunResult.OutputLines,
                ToolOutputTruncator.k_maximumCommandLineCount,
                ToolOutputTruncator.k_maximumOutputCharacterCount,
                out int keptLineCount);

            string truncationNote = ToolOutputTruncator.BuildTruncationNote(keptLineCount,
                commandRunResult.OutputLines.Count, "output lines", k_hintAboutTheTruncatedHead);

            if (truncationNote.Length == 0)
            {
                return outputTail;
            }

            var outputBuilder = new StringBuilder(truncationNote);
            outputBuilder.Append('\n');
            outputBuilder.Append(outputTail);
            return outputBuilder.ToString();
        }
    }
}
