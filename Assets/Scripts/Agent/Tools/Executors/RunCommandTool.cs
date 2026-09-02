using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Amberline.Agent
{
    // run_command(command) - the generic verification loop. This is what lets the agent check its
    // own work on any project in any language: `dotnet build`, `npm run build`, `pytest`. Nothing
    // in here knows what Unity is, and that is deliberate - Unity is only the host.
    //
    // Four decisions:
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
    //
    // 4. A COMMAND THAT CANNOT WORK ON WINDOWS IS REFUSED BEFORE IT RUNS. The model was trained on
    //    bash and reaches for `ls`, `cat` and `touch`; the shell here is cmd.exe, which answers 9009
    //    and nothing else. Worse, a shell redirect CREATES THE FILE BEFORE the command runs, so a
    //    failed `echo ... > src/Hello.py` leaves an empty file and an invented folder behind. Both
    //    are caught up front and answered with the tool the model should have called instead.
    public class RunCommandTool : IToolExecutor
    {
        readonly CommandRunner _commandRunner;

        static readonly ToolDefinition k_toolDefinition = new ToolDefinition(
            name: "run_command",
            parameterNames: new[] { "command" },
            isMutating: true,
            isCommand: true,
            maximumResponseTokens: 512);

        // What to say instead of each bash command the model is most likely to reach for. The value
        // names the tool or the cmd.exe command that does the same job, because a refusal that only
        // says "no" sends the model looking for a synonym that fails in exactly the same way.
        static readonly Dictionary<string, string> k_windowsAnswerForEachBashCommand =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ls", "call the list_dir tool" },
                { "cat", "call the read_file tool" },
                { "head", "call read_file with start_line and end_line" },
                { "tail", "call read_file with start_line and end_line" },
                { "grep", "call the grep tool" },
                { "sed", "call the edit_file tool" },
                { "awk", "call read_file and work the text out yourself" },
                { "touch", "call the write_file tool" },
                { "rm", "the command here is del, or rmdir /s /q for a folder" },
                { "cp", "the command here is copy" },
                { "mv", "the command here is move" },
                { "pwd", "you are already in the project folder; there is nowhere else to be" },
                { "which", "the command here is where" },
                { "chmod", "Windows has no file modes, so there is nothing to change" },
                { "chown", "Windows has no file owners to set this way" },
                { "ln", "Windows has no ln; copy the file instead" },
                { "python3", "the command here is python" },
                { "pip3", "the command here is pip" },
                { "wget", "the command here is curl" },
                { "man", "there is no man here; pass /? to the command instead" },
                { "clear", "there is nothing for a command to clear" },
                { "df", "there is no df here" },
                { "du", "there is no du here" }
            };

        // Said after every failed command. It deliberately does NOT ask for the same command again:
        // RepeatedCallDetector refuses an identical repeat, so a hint that asks for one walks the
        // agent straight into a refusal and the run stalls there.
        const string k_hintAfterAFailedCommand =
            "Read the output above and fix what caused it. Then check the fix with a DIFFERENT command, or with read_file.";

        const string k_hintAboutTheTruncatedHead =
            "Only the end of the output is shown, because that is where a build puts its errors.";

        // cmd.exe answers this when the first word of the line is not a command it can find.
        const int k_exitCodeForACommandThatWasNotFound = 9009;

        const string k_reminderAboutTheShell = "The shell here is Windows cmd.exe, not bash.";

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

            if (TryExplainWhyThisCommandCannotRunOnWindows(commandText, out string explanationOfWhyItCannotRun))
            {
                return ToolResult.Failure(explanationOfWhyItCannotRun);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var commandRunResult = await _commandRunner.RunAsync(commandText, cancellationToken);
            return BuildToolResultForCommandRun(commandText, commandRunResult);
        }

        // Answered here rather than by running it and reading the failure, because both cases below
        // do damage on the way to their exit code: the bash command wastes a whole round trip, and
        // the redirect leaves a real empty file on disk that the agent then believes it wrote.
        static bool TryExplainWhyThisCommandCannotRunOnWindows(string commandText, out string explanation)
        {
            string firstWordOfTheCommand = TakeFirstWordOfCommand(commandText);

            if (k_windowsAnswerForEachBashCommand.TryGetValue(firstWordOfTheCommand, out string windowsAnswer))
            {
                explanation = $"{firstWordOfTheCommand} does not exist here. {k_reminderAboutTheShell} Instead, {windowsAnswer}.";
                return true;
            }

            if (DoesCommandRedirectIntoAFile(commandText))
            {
                explanation =
                    "To create or overwrite a file, call write_file with the whole content. " +
                    "A shell redirect creates the file BEFORE the command runs, so a command that then fails leaves an empty file behind.";
                return true;
            }

            if (DoesCommandStepOutOfTheWorkspace(commandText))
            {
                explanation =
                    "the path contains \"..\", which leaves the project folder. Every tool refuses those, and so does this one - " +
                    "otherwise a command is a way around the sandbox the other six tools enforce. " +
                    "The command already runs IN the project folder, so name the file relative to it. " +
                    "Call list_dir with path . if you are not sure the file is there.";
                return true;
            }

            explanation = null;
            return false;
        }

        // The sandbox is a promise about the whole agent, not about every tool but this one. Measured:
        // told the file it wanted was not where it looked, the model answered `python ../hello.py`
        // and cmd.exe ran it one folder above the workspace - outside everything PathSandbox exists
        // to protect - and the only reason nothing was damaged is that the file was not there either.
        //
        // Only a ".." that is really a path segment counts. `git log a...b` and `archive..tar` are
        // ordinary arguments, and refusing them would cost a round trip for nothing.
        static bool DoesCommandStepOutOfTheWorkspace(string commandText)
        {
            for (int characterIndex = commandText.IndexOf("..", StringComparison.Ordinal);
                 characterIndex >= 0;
                 characterIndex = commandText.IndexOf("..", characterIndex + 1, StringComparison.Ordinal))
            {
                if (IsAPathSegmentBoundary(commandText, characterIndex - 1) &&
                    IsAPathSegmentBoundary(commandText, characterIndex + 2))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsAPathSegmentBoundary(string commandText, int characterIndex)
        {
            // Past either end of the line is a boundary: `cd ..` ends there, and a command may open
            // with the path it is about.
            if (characterIndex < 0 || characterIndex >= commandText.Length)
            {
                return true;
            }

            char character = commandText[characterIndex];
            return character == '/' || character == '\\' || character == ' ' || character == '"' || character == '\'';
        }

        static string TakeFirstWordOfCommand(string commandText)
        {
            int endOfTheFirstWord = commandText.IndexOf(' ');
            return endOfTheFirstWord < 0 ? commandText : commandText.Substring(0, endOfTheFirstWord);
        }

        // True only for a redirect that writes a FILE. `2>&1` merges the streams and `>nul` throws
        // the output away - neither creates anything, and both are ordinary parts of a real command.
        //
        // And only a `>` OUTSIDE quotes is a redirect at all. cmd.exe reads a quoted one as text, so
        // this has to as well: `git log --pretty=format:"%h <%ae>"` and `git commit -m "a > b"` are
        // ordinary commands, and refusing them with a message about write_file - which has nothing
        // to do with what was asked - sent the agent looking for a spelling that never existed.
        static bool DoesCommandRedirectIntoAFile(string commandText)
        {
            int lastRedirectIndex = FindLastRedirectOutsideQuotes(commandText);
            if (lastRedirectIndex < 0) return false;

            string targetOfTheRedirect = commandText.Substring(lastRedirectIndex + 1).Trim();

            if (targetOfTheRedirect.Length == 0) return false;
            if (targetOfTheRedirect.StartsWith("&", StringComparison.Ordinal)) return false;

            return !targetOfTheRedirect.Equals("nul", StringComparison.OrdinalIgnoreCase);
        }

        // A double quote opens the quoted run and the next one closes it, which is exactly how
        // cmd.exe reads the line. Anything between the two is text, redirects included.
        static int FindLastRedirectOutsideQuotes(string commandText)
        {
            bool isInsideQuotes = false;
            int lastRedirectIndex = -1;

            for (int characterIndex = 0; characterIndex < commandText.Length; characterIndex++)
            {
                char currentCharacter = commandText[characterIndex];

                if (currentCharacter == '"')
                {
                    isInsideQuotes = !isInsideQuotes;
                    continue;
                }

                if (currentCharacter == '>' && !isInsideQuotes)
                    lastRedirectIndex = characterIndex;
            }

            return lastRedirectIndex;
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

            return ToolResult.Failure(
                $"exit code {commandRunResult.ExitCode} from: {commandText}\n{outputTail}\n" +
                ExplainExitCode(commandRunResult.ExitCode, commandText));
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

        // 9009 is the one exit code worth naming. cmd.exe uses it for "that is not a command", and
        // a bare number tells the model nothing - so it retries spelling variants of a command that
        // was never going to exist. Every other code belongs to the command itself and means
        // whatever that command says it means, so its own output is the better guide.
        //
        // What it must NOT say is "not installed". 9009 means the first word was not on the PATH
        // this process inherited, and that PATH was taken when the editor started - so a tool
        // installed since then is missing from it while being perfectly present on the machine.
        // The old wording asserted the stronger claim, and the model believed it for the whole run.
        static string ExplainExitCode(int exitCode, string commandText)
        {
            if (exitCode != k_exitCodeForACommandThatWasNotFound)
            {
                return k_hintAfterAFailedCommand;
            }

            string firstWordOfTheCommand = TakeFirstWordOfCommand(commandText);

            return $"{firstWordOfTheCommand} was not found on the PATH this program inherited, which is not the same " +
                   $"as not being installed. Check it with: where {firstWordOfTheCommand}. {k_reminderAboutTheShell} " +
                   "For files use read_file, write_file, list_dir and grep instead of shell commands.";
        }
    }
}
