using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Amberline.Agent
{
    /// <summary>
    /// What one shell command produced. A non-zero exit code is an ordinary outcome carried in
    /// <see cref="ExitCode"/> - only a command that could not be started at all, or one that had to
    /// be killed, is reported as something other than a finished run.
    /// </summary>
    public class CommandRunResult
    {
        /// <summary>False when the shell itself could not be launched. Everything else is then meaningless.</summary>
        public bool DidStart { get; }

        /// <summary>The process exit code. 0 means the command succeeded; anything else did not.</summary>
        public int ExitCode { get; }

        /// <summary>True when the command outlived its hard timeout and its process tree was killed.</summary>
        public bool DidTimeOut { get; }

        /// <summary>stdout and stderr merged in arrival order. Never null.</summary>
        public IReadOnlyList<string> OutputLines { get; }

        /// <summary>Why the shell could not be launched. Empty unless <see cref="DidStart"/> is false.</summary>
        public string StartFailureMessage { get; }

        CommandRunResult(bool didStart, int exitCode, bool didTimeOut, IReadOnlyList<string> outputLines, string startFailureMessage)
        {
            DidStart = didStart;
            ExitCode = exitCode;
            DidTimeOut = didTimeOut;
            OutputLines = outputLines ?? new List<string>();
            StartFailureMessage = startFailureMessage ?? string.Empty;
        }

        public static CommandRunResult Finished(int exitCode, IReadOnlyList<string> outputLines)
        {
            return new CommandRunResult(true, exitCode, false, outputLines, null);
        }

        /// <summary>Killed after the hard timeout. Whatever it printed before that is still carried,
        /// because a hung build usually says why in the last line it managed to write.</summary>
        public static CommandRunResult TimedOut(IReadOnlyList<string> outputLines)
        {
            return new CommandRunResult(true, -1, true, outputLines, null);
        }

        public static CommandRunResult CouldNotStart(string startFailureMessage)
        {
            return new CommandRunResult(false, -1, false, null, startFailureMessage);
        }
    }

    // Runs one shell command in the workspace root and streams what it prints back to the terminal
    // while it is still running.
    //
    // Six properties of this class are load-bearing, and every one of them is here because the
    // obvious simpler version has a failure mode a coding agent hits within a day:
    //
    // 1. IT RUNS ON THE THREAD POOL. A `dotnet build` blocks for tens of seconds, and blocking the
    //    Unity main thread for that long freezes the terminal that is supposed to be showing the
    //    build happen.
    //
    // 2. STDIN IS CLOSED THE MOMENT THE PROCESS STARTS. A command that asks a question - "overwrite
    //    [y/N]?", a credential prompt - has nobody to answer it. With stdin left open it waits for
    //    an answer forever and the run dies at the timeout instead of at the prompt. Closed, the
    //    read hits end of file and the command fails immediately with a message the model can read.
    //
    // 3. STDOUT AND STDERR ARE MERGED IN ARRIVAL ORDER. A compiler writes its errors to stderr and
    //    the context that produced them to stdout. Kept apart, the model gets a list of errors with
    //    no idea which file each belongs to.
    //
    // 4. OUTPUT IS STREAMED, NOT COLLECTED AND HANDED OVER AT THE END. Ten to thirty seconds of a
    //    blank screen reads as a hang, and watching the agent work is the whole point of this
    //    milestone. The lines are posted to the main thread here rather than by the subscriber,
    //    because a view that has to remember to marshal will eventually forget.
    //
    // 5. THE WHOLE PROCESS TREE IS KILLED, not just the shell. cmd.exe is the parent of the real
    //    compiler; killing only the parent leaves the compiler running, holding file locks, with
    //    nothing left that can ever stop it.
    //
    // 6. THERE IS A HARD TIMEOUT. Cancellation covers the user changing their mind; the timeout
    //    covers a command that is never going to finish and a user who has walked away.
    public class CommandRunner
    {
        readonly string _workspaceRootPath;
        readonly Action<string> _showOutputLineToTheUser;

        // Captured on the main thread at construction, so output produced on a thread-pool thread
        // can be handed back to the UI without every caller having to marshal it itself.
        readonly SynchronizationContext _mainThreadContext;

        /// <summary>How long any one command may run before its process tree is killed.</summary>
        public const int k_maximumCommandSeconds = 180;

        const int k_millisecondsBetweenExitChecks = 50;
        const int k_millisecondsToWaitForTheProcessToActuallyGo = 5000;
        const int k_millisecondsToWaitForTaskkill = 5000;

        // Stored lines are capped so a command that prints forever cannot fill memory. The tail is
        // kept, because only the tail is ever sent back to the model.
        const int k_maximumStoredOutputLineCount = 4000;

        // Shown lines are capped far lower: the terminal holds 400 line elements in total, and a
        // build that pushes them all out would erase the run that led up to it.
        const int k_maximumOutputLinesShownInTheTerminal = 150;

        public CommandRunner(string workspaceRootPath, Action<string> showOutputLineToTheUser)
        {
            _workspaceRootPath = workspaceRootPath;
            _showOutputLineToTheUser = showOutputLineToTheUser;
            _mainThreadContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Runs <paramref name="commandText"/> through the platform shell with the workspace root as
        /// the working directory. Never throws for an ordinary failure - a command that fails, that
        /// times out, or that cannot even start all come back as a result. Throws
        /// <see cref="OperationCanceledException"/> only when the user cancelled, and only after the
        /// process tree has actually been killed.
        /// </summary>
        public UniTask<CommandRunResult> RunAsync(string commandText, CancellationToken cancellationToken)
        {
            return UniTask.RunOnThreadPool(() => RunCommandAndCollectOutput(commandText, cancellationToken), true, cancellationToken);
        }

        CommandRunResult RunCommandAndCollectOutput(string commandText, CancellationToken cancellationToken)
        {
            var outputCollector = new CommandOutputCollector(_mainThreadContext, _showOutputLineToTheUser);
            Process shellProcess;

            try
            {
                shellProcess = StartShellProcess(commandText);
            }
            catch (Exception exception)
            {
                return CommandRunResult.CouldNotStart($"{exception.GetType().Name}: {exception.Message}");
            }

            if (shellProcess == null)
            {
                return CommandRunResult.CouldNotStart("the platform shell did not start and gave no reason.");
            }

            try
            {
                StartReadingBothOutputStreams(shellProcess, outputCollector);
                CloseStandardInputSoAPromptHitsEndOfFile(shellProcess);

                return WaitForTheCommandOrKillIt(shellProcess, outputCollector, cancellationToken);
            }
            finally
            {
                shellProcess.Dispose();
            }
        }

        Process StartShellProcess(string commandText)
        {
            var shellStartInformation = new ProcessStartInfo
            {
                FileName = IsRunningOnWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = BuildShellArguments(commandText),
                WorkingDirectory = _workspaceRootPath,

                // All three redirections together are what make this class possible at all:
                // without them there is no output to stream and no stdin to close.
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,

                // Modern build tools write UTF-8, and a compiler message is what this product
                // exists to carry, so UTF-8 wins the one choice available here. The cost is real
                // and one-sided: an OLD Windows tool that still writes the console OEM code page -
                // ping, findstr - comes back as mojibake in its non-ASCII text on a non-English
                // system. Measured, not guessed. ASCII is identical either way.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            return Process.Start(shellStartInformation);
        }

        // /d skips whatever the user's AutoRun registry key would otherwise run first, so a
        // developer with a custom cmd profile does not get their banner mixed into the build log.
        static string BuildShellArguments(string commandText)
        {
            if (IsRunningOnWindows())
            {
                return "/d /c " + commandText;
            }

            return "-c \"" + commandText.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // Deliberately not Application.platform: this runs on a thread-pool thread and most of the
        // Unity API may only be touched from the main one.
        static bool IsRunningOnWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        static void StartReadingBothOutputStreams(Process shellProcess, CommandOutputCollector outputCollector)
        {
            shellProcess.OutputDataReceived += outputCollector.HandleOutputLineReceived;
            shellProcess.ErrorDataReceived += outputCollector.HandleOutputLineReceived;

            shellProcess.BeginOutputReadLine();
            shellProcess.BeginErrorReadLine();
        }

        // Rule 2 in the class header. An interactive prompt must fail fast rather than wait forever
        // for input that nobody in this product can ever give it.
        static void CloseStandardInputSoAPromptHitsEndOfFile(Process shellProcess)
        {
            try
            {
                shellProcess.StandardInput.Close();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CommandRunner] Standard input could not be closed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        CommandRunResult WaitForTheCommandOrKillIt(Process shellProcess, CommandOutputCollector outputCollector,
            CancellationToken cancellationToken)
        {
            var elapsedTime = Stopwatch.StartNew();

            while (!shellProcess.WaitForExit(k_millisecondsBetweenExitChecks))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    KillTheWholeProcessTree(shellProcess);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (elapsedTime.Elapsed.TotalSeconds >= k_maximumCommandSeconds)
                {
                    KillTheWholeProcessTree(shellProcess);
                    return CommandRunResult.TimedOut(outputCollector.TakeCollectedLines());
                }
            }

            // The documented second wait, with no timeout: the timed overload can return before the
            // asynchronous readers have flushed, and the last line of a build is the one that says
            // what went wrong.
            shellProcess.WaitForExit();

            return CommandRunResult.Finished(shellProcess.ExitCode, outputCollector.TakeCollectedLines());
        }

        // Rule 5 in the class header. taskkill /T walks the tree; Process.Kill would only take the
        // cmd.exe wrapper and leave the compiler underneath it running.
        static void KillTheWholeProcessTree(Process shellProcess)
        {
            if (HasTheProcessAlreadyExited(shellProcess))
            {
                return;
            }

            if (IsRunningOnWindows() && TryKillProcessTreeWithTaskkill(shellProcess.Id))
            {
                WaitForTheProcessToActuallyGo(shellProcess);
                return;
            }

            try
            {
                shellProcess.Kill();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CommandRunner] The command could not be killed: {exception.GetType().Name}: {exception.Message}");
            }

            WaitForTheProcessToActuallyGo(shellProcess);
        }

        static bool HasTheProcessAlreadyExited(Process shellProcess)
        {
            try
            {
                return shellProcess.HasExited;
            }
            catch (Exception)
            {
                // A process we can no longer ask about is one we can no longer kill either.
                return true;
            }
        }

        static bool TryKillProcessTreeWithTaskkill(int processId)
        {
            try
            {
                var taskkillStartInformation = new ProcessStartInfo("taskkill", $"/PID {processId} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var taskkillProcess = Process.Start(taskkillStartInformation))
                {
                    if (taskkillProcess == null)
                    {
                        return false;
                    }

                    taskkillProcess.WaitForExit(k_millisecondsToWaitForTaskkill);
                    return taskkillProcess.HasExited && taskkillProcess.ExitCode == 0;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CommandRunner] taskkill did not run: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        // Killing is a request, not an event. Waiting for it to land is what turns "we asked it to
        // stop" into "it is gone", which is the only claim worth making about an orphaned compiler.
        static void WaitForTheProcessToActuallyGo(Process shellProcess)
        {
            try
            {
                if (!shellProcess.WaitForExit(k_millisecondsToWaitForTheProcessToActuallyGo))
                {
                    Debug.LogError($"[CommandRunner] The command was killed but was still running after {k_millisecondsToWaitForTheProcessToActuallyGo} ms.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CommandRunner] Waiting for the killed command failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // Collects both output streams into ONE list in arrival order, and shows each line as it
        // arrives. The two stream handlers run on different thread-pool threads, so every touch of
        // the list is locked - without that, two lines landing at once corrupt it.
        class CommandOutputCollector
        {
            readonly Queue<string> _collectedLines = new Queue<string>();
            readonly object _lockOverCollectedLines = new object();
            readonly SynchronizationContext _mainThreadContext;
            readonly Action<string> _showOutputLineToTheUser;

            int _amountOfLinesShownToTheUser;

            const string k_noticeThatTheRestIsNotShown =
                "  ... the command is still printing; the rest is not shown here, and its last lines still go back to the agent";

            public CommandOutputCollector(SynchronizationContext mainThreadContext, Action<string> showOutputLineToTheUser)
            {
                _mainThreadContext = mainThreadContext;
                _showOutputLineToTheUser = showOutputLineToTheUser;
            }

            public void HandleOutputLineReceived(object sender, DataReceivedEventArgs eventArguments)
            {
                // A null Data is how the framework says the stream reached its end, not an empty line.
                if (eventArguments.Data == null)
                {
                    return;
                }

                string lineToShow = RememberOneOutputLineAndDecideWhatToShow(eventArguments.Data);
                ShowOneOutputLineToTheUser(lineToShow);
            }

            // Both stream handlers run on their own thread-pool thread, so the list AND the shown
            // counter are read and written under one lock. The posting itself is left outside it -
            // holding a lock across a hand-off to another thread is how deadlocks start.
            // Returns the line to put on screen, or null when nothing should be shown.
            string RememberOneOutputLineAndDecideWhatToShow(string outputLine)
            {
                lock (_lockOverCollectedLines)
                {
                    _collectedLines.Enqueue(outputLine);

                    while (_collectedLines.Count > k_maximumStoredOutputLineCount)
                    {
                        _collectedLines.Dequeue();
                    }

                    if (_amountOfLinesShownToTheUser > k_maximumOutputLinesShownInTheTerminal)
                    {
                        return null;
                    }

                    _amountOfLinesShownToTheUser++;

                    // The one line past the cap says why the log stops, so a truncated terminal is
                    // never mistaken for a command that stopped printing.
                    return _amountOfLinesShownToTheUser > k_maximumOutputLinesShownInTheTerminal
                        ? k_noticeThatTheRestIsNotShown
                        : outputLine;
                }
            }

            void ShowOneOutputLineToTheUser(string lineToShow)
            {
                if (lineToShow == null || _showOutputLineToTheUser == null || _mainThreadContext == null)
                {
                    return;
                }

                _mainThreadContext.Post(postedLine => _showOutputLineToTheUser((string)postedLine), lineToShow);
            }

            /// <summary>A snapshot of everything collected so far, oldest first.</summary>
            public IReadOnlyList<string> TakeCollectedLines()
            {
                lock (_lockOverCollectedLines)
                {
                    return new List<string>(_collectedLines);
                }
            }
        }
    }
}
