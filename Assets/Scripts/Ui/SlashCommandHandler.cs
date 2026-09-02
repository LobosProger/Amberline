using System;
using System.Collections.Generic;
using System.IO;
using Amberline.Agent;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Ui
{
    /// <summary>
    /// Everything the terminal answers itself, without going near the model: /help, /cwd, /cd,
    /// /context, /tools, /approve-mode, /compact, /undo, /clear and /exit.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="TerminalCliController"/>, which had grown to a thousand lines around
    /// three unrelated jobs. This one owns the command table and nothing else: it never touches the
    /// thought block, the tool card or the cancellation source directly, and reaches the three
    /// things it cannot do without through <see cref="ITerminalCommandHost"/>.
    /// <para>
    /// A plain class rather than a MonoBehaviour, because it holds no scene state. The controller
    /// builds one in Awake and hands it the views it needs.
    /// </para>
    /// </remarks>
    public class SlashCommandHandler
    {
        readonly TerminalView _terminalView;
        readonly AgentRunner _agentRunner;
        readonly ITerminalCommandHost _host;

        // The pool Tab completes against, and the switch below, in the order /help prints them.
        // Adding a command means adding it in both places - the list is what the input row offers,
        // and an offered command that falls through to the default reads as a bug.
        static readonly string[] k_slashCommands =
        {
            "/help", "/cwd", "/cd", "/context", "/tools", "/approve-mode", "/compact", "/undo", "/clear", "/exit"
        };

        public SlashCommandHandler(TerminalView terminalView, AgentRunner agentRunner, ITerminalCommandHost host)
        {
            _terminalView = terminalView;
            _agentRunner = agentRunner;
            _host = host;
        }

        /// <summary>The command names the input row offers for completion.</summary>
        public IReadOnlyList<string> CommandNames => k_slashCommands;

        /// <summary>
        /// Handles the submitted line when it is a slash command and reports whether it did. A line
        /// that does not start with a slash is not ours and goes to the agent; an unknown command
        /// IS ours - it is answered with a line rather than sent to the model, which would otherwise
        /// spend a whole turn on a typo.
        /// </summary>
        public async UniTask<bool> TryHandleAsync(string submittedCommand)
        {
            if (!submittedCommand.StartsWith("/", StringComparison.Ordinal)) return false;

            var commandName = submittedCommand.Split(' ')[0].ToLowerInvariant();

            switch (commandName)
            {
                case "/help":
                    ShowHelp();
                    return true;

                case "/cwd":
                    ShowWorkspace();
                    return true;

                case "/cd":
                    ChangeWorkspaceFolder(submittedCommand);
                    return true;

                case "/context":
                    ShowContextUsage();
                    return true;

                case "/tools":
                    ShowCallableTools();
                    return true;

                case "/approve-mode":
                    ToggleApprovalMode();
                    return true;

                case "/compact":
                    await CompactConversationAsync();
                    return true;

                case "/undo":
                    UndoLastFileChange();
                    return true;

                case "/clear":
                    ClearScreenAndConversation();
                    return true;

                case "/exit":
                    QuitApplication();
                    return true;

                default:
                    _terminalView.AppendLineInstant($"unknown command: {commandName}. type /help for the list.", TerminalLineKind.Error);
                    return true;
            }
        }

        void ShowHelp()
        {
            _terminalView.AppendLineInstant("/help     show this list", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/cwd      show the workspace the agent is sandboxed to", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/cd <absolute path>  move the agent to another folder - this clears the conversation", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/context  show how much of the model's context window is used", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/tools    show the tools the agent can call right now", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/approve-mode  switch between asking about every edit and letting edits through", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/compact  summarise the older part of the conversation to free up context", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/undo     put the last file change the agent made back the way it was", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/clear    clear the screen and forget the conversation", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/exit     quit", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("anything else is sent to the agent. esc cancels a running turn.", TerminalLineKind.Notice);
        }

        void ShowWorkspace()
        {
            _terminalView.AppendLineInstant(_host.WorkspaceFolderPath, TerminalLineKind.Notice);
        }

        // Parsing and refusing live here; moving the sandbox lives on the host, because it also
        // has to rewrite both bars and the remembered preference.
        void ChangeWorkspaceFolder(string submittedCommand)
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there is no workspace to move.", TerminalLineKind.Error);
                return;
            }

            if (_agentRunner.ApprovalGate.IsWaitingForTheUser)
            {
                _terminalView.AppendLineInstant("cd: answer the approval card that is open first, or press esc.", TerminalLineKind.Error);
                return;
            }

            string requestedFolderPath = ReadArgumentAfterTheCommandName(submittedCommand);

            if (string.IsNullOrEmpty(requestedFolderPath))
            {
                _terminalView.AppendLineInstant("cd: usage: /cd <absolute path>", TerminalLineKind.Notice);
                _terminalView.AppendLineInstant($"the workspace is {_host.WorkspaceFolderPath}", TerminalLineKind.Notice);
                return;
            }

            if (!TryResolveRequestedWorkspaceFolderPath(requestedFolderPath, out string fullFolderPath, out string failureReason))
            {
                _terminalView.AppendLineInstant($"cd: {failureReason}", TerminalLineKind.Error);
                return;
            }

            _host.ApplyWorkspaceFolderPath(fullFolderPath);
        }

        // Everything after the first space, rather than the second token: splitting on spaces would
        // cut a path like C:\My Folder\src in half. Surrounding quotes come off as well, because a
        // path with a space in it is exactly when people reach for them.
        static string ReadArgumentAfterTheCommandName(string submittedCommand)
        {
            int firstSpaceIndex = submittedCommand.IndexOf(' ');
            if (firstSpaceIndex < 0) return string.Empty;

            return submittedCommand.Substring(firstSpaceIndex + 1).Trim().Trim('"').Trim();
        }

        // Path.GetFullPath throws on genuinely malformed input rather than returning anything, so
        // the failure is turned into a line the user can read instead of an exception in the log.
        static bool TryResolveRequestedWorkspaceFolderPath(string requestedFolderPath, out string fullFolderPath, out string failureReason)
        {
            fullFolderPath = string.Empty;
            failureReason = null;

            if (!Path.IsPathRooted(requestedFolderPath))
            {
                failureReason = $"{requestedFolderPath} is not an absolute path - it has to start from a drive, like C:\\projects\\my-app";
                return false;
            }

            try
            {
                fullFolderPath = Path.GetFullPath(requestedFolderPath);
            }
            catch (Exception exception)
            {
                failureReason = $"{requestedFolderPath} is not a usable path: {exception.Message}";
                return false;
            }

            if (!Directory.Exists(fullFolderPath))
            {
                failureReason = $"there is no folder at {fullFolderPath}";
                return false;
            }

            return true;
        }

        void ShowContextUsage()
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there is no context to report.", TerminalLineKind.Error);
                return;
            }

            ContextUsage contextUsage = _agentRunner.GetContextUsage();
            if (contextUsage.MaxTokens <= 0)
            {
                _terminalView.AppendLineInstant($"context: {contextUsage.UsedTokens} tokens used. the model has not reported its window yet.", TerminalLineKind.Notice);
                return;
            }

            int fillPercentage = Mathf.RoundToInt(contextUsage.FillRatio * 100f);
            _terminalView.AppendLineInstant($"context: {contextUsage.UsedTokens} of {contextUsage.MaxTokens} usable tokens ({fillPercentage}% full)", TerminalLineKind.Notice);
        }

        // Every tool the model can reach, in the order the grammar offers them. What stands between
        // the model and the file system is the approval card, not this list.
        void ShowCallableTools()
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there are no tools to list.", TerminalLineKind.Error);
                return;
            }

            var callableToolNames = _agentRunner.GetNamesOfCallableTools();
            string toolNamesText = callableToolNames.Count == 0 ? "(none)" : string.Join(", ", callableToolNames);

            _terminalView.AppendLineInstant($"tools ..... {toolNamesText}", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant($"approval .. {DescribeCurrentPermissionMode()}", TerminalLineKind.Notice);
        }

        // Two modes, one key. The line about commands is printed every single time rather than
        // only in the loose mode, because the whole value of this switch is that the user knows
        // exactly what they just gave up - and commands are the one thing they did not.
        void ToggleApprovalMode()
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there is no approval mode to change.", TerminalLineKind.Error);
                return;
            }

            var approvalGate = _agentRunner.ApprovalGate;

            var nextPermissionMode = approvalGate.CurrentPermissionMode == PermissionMode.AskEveryTime
                ? PermissionMode.AutoApproveEdits
                : PermissionMode.AskEveryTime;

            approvalGate.SetPermissionMode(nextPermissionMode);

            _terminalView.AppendLineInstant($"approve mode ..... {DescribeCurrentPermissionMode()}", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("commands ......... always asked about, in every mode", TerminalLineKind.Notice);
        }

        string DescribeCurrentPermissionMode()
        {
            return _agentRunner.ApprovalGate.CurrentPermissionMode == PermissionMode.AskEveryTime
                ? "ask every time - every write and every command shows a card"
                : "auto approve edits - writes inside the workspace go through without a card";
        }

        // The conversation is folded down while the user waits, because it is a real generation.
        // Nothing is half rewritten: either the summary replaces the older messages or nothing
        // changes at all, and the context line afterwards is the proof of which happened.
        async UniTask CompactConversationAsync()
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there is nothing to compact.", TerminalLineKind.Error);
                return;
            }

            _terminalView.AppendLineInstant("compacting the older part of the conversation...", TerminalLineKind.Notice);

            bool wasTheConversationRewritten =
                await _agentRunner.CompactConversationAsync(_host.GetCancellationTokenOfCurrentTurn());

            if (!wasTheConversationRewritten)
            {
                _terminalView.AppendLineInstant("compact: nothing was changed - the conversation is short enough, or the summary came back empty.",
                    TerminalLineKind.Notice);
                return;
            }

            ShowContextUsage();
        }

        // One change per call, the most recent first. A user who wants two changes gone types it
        // twice, which is also the only way they can see what each step put back.
        void UndoLastFileChange()
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there is nothing to undo.", TerminalLineKind.Error);
                return;
            }

            if (!_agentRunner.TryUndoLastFileChange(out string undoneDisplayPath, out string failureReason))
            {
                _terminalView.AppendLineInstant($"undo: {failureReason}", TerminalLineKind.Error);
                return;
            }

            _terminalView.AppendLineInstant($"undo: {undoneDisplayPath} is back the way it was.", TerminalLineKind.Notice);
        }

        // Both halves, always. Clearing only the screen would leave the agent remembering a
        // conversation the user can no longer see, which is the worst of both.
        void ClearScreenAndConversation()
        {
            // Dropped BEFORE the lines are cut out, so neither one is left holding a label that is
            // no longer in the panel.
            _host.PrepareScreenForClearing();

            _terminalView.ClearAllLines();

            if (_agentRunner != null)
                _agentRunner.ClearConversation();
        }

        void QuitApplication()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
