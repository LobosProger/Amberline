using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Amberline.Agent;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Amberline.Ui
{
    /// <summary>
    /// Drives the terminal: runs the boot sequence, routes what the user submits to either a
    /// slash command or the agent, and keeps the views in sync. This is the only place that
    /// knows the order things happen in - the views themselves hold no session state.
    /// <para>
    /// The dependency on the agent runs one way: this controller calls into
    /// <see cref="AgentRunner"/>, and nothing in the agent core knows this namespace exists.
    /// </para>
    /// </summary>
    public class TerminalCliController : MonoBehaviour
    {
        [Header("Views")]
        [SerializeField] TerminalView _terminalView;
        [SerializeField] CommandInputView _commandInputView;
        [SerializeField] SpinnerView _spinnerView;
        [Tooltip("Keeps the log pinned to the bottom. Without it, a user who scrolled up to read stays there and never sees the answer to what they type next.")]
        [SerializeField] TerminalAutoScrollView _autoScrollView;
        [Space]
        [SerializeField] TopBarView _topBarView;
        [SerializeField] StatusBarView _statusBarView;
        [Space]
        [Tooltip("Draws the card that stops the agent until the user answers. Without it a write can never be approved.")]
        [SerializeField] ApprovalCardView _approvalCardView;
        [Tooltip("Draws what a write actually changed. Without it a write is only ever reported as a one-line count.")]
        [SerializeField] DiffView _diffView;

        [Header("Agent")]
        [SerializeField] AgentRunner _agentRunner;

        [Header("Workspace")]
        [Tooltip("Folder the agent is allowed to work in. Set, it always wins. Left empty, the folder /cd was last pointed at is used, and failing that the folder that contains this Unity project.")]
        [SerializeField] string _workspaceFolderPath;
        [Tooltip("Remember the folder /cd was last pointed at and use it again next time Play is pressed. Only used when the field above is empty.")]
        [SerializeField] bool _shouldRememberTheWorkspaceFolderBetweenSessions = true;

        string _resolvedWorkspaceFolderPath;

        // Cancels whatever the current turn is doing. Replaced at the start of every turn so a
        // cancel can never reach back and kill the turn after it.
        CancellationTokenSource _currentTurnCancellationSource;

        bool _isBootSequenceRunning;
        bool _isTurnInProgress;

        // The folded line the model's current plan is being written into. Created on the first
        // token rather than up front, so an iteration that produces no plan leaves no empty box
        // behind, and cleared once the plan is final so the next iteration starts a line of its own.
        ThinkingBlock _currentThinkingBlock;

        // The card of the tool that is running right now, kept alive so it can spin while the tool
        // works and be finished off with its outcome. Before this, the card was printed once and
        // then sat there motionless for the whole of a thirty-second build.
        readonly LineSpinner _runningToolSpinner = new LineSpinner();
        string _textOfTheRunningToolCard = string.Empty;

        // Released the moment the first text lands, and again when the turn ends whatever the
        // outcome. The spinner waits on it, so it can never keep spinning over visible text.
        UniTaskCompletionSource<bool> _firstAnswerTextSignal;

        // The request a card is being built for right now. The build is asynchronous, so by the time
        // it finishes the run may already have been cancelled and this request resolved - comparing
        // against it is what stops a card appearing for a question nobody is waiting on.
        ApprovalRequest _approvalRequestWaitingForACard;

        // What the status bar shows once the current turn is over. A cancelled turn leaves
        // "cancelled" up until the next one, instead of flashing it for a single frame.
        string _stateTextToShowWhenTheTurnEnds = k_idleStateText;

        const float k_bootLineCharacterDelaySeconds = 0.008f;
        const string k_idleStateText = "idle";
        const string k_workingStateText = "working";
        const string k_cancelledStateText = "cancelled";
        const string k_cancelledNoticeText = "cancelled";

        // A tool result can be a whole file. The log shows the head of it and says how much it is
        // not showing - the model gets the full text, the user gets a line they can read.
        const int k_maximumCharactersOfAToolResultOnScreen = 140;
        const int k_maximumCharactersOfAToolArgumentOnScreen = 90;

        // Replaces the spinner frame once the tool is done, so a finished card reads as finished
        // rather than as one frozen mid-spin.
        const string k_toolCardSuffixWhenItWorked = "  ok";
        const string k_toolCardSuffixWhenItFailed = "  failed";

        // Checked in this order, so grep shows what it searched for rather than the folder it
        // searched in, and read_file shows the file rather than nothing. "summary" is deliberately
        // absent: it belongs to finish, whose summary is the answer and is printed on its own.
        static readonly string[] k_argumentNamesWorthShowingOnACard = { "pattern", "command", "path" };

        static readonly string[] k_slashCommands =
            { "/help", "/cwd", "/cd", "/context", "/tools", "/approve-mode", "/compact", "/undo", "/clear", "/exit" };

        const int k_maximumCharactersOfACommandOutputLineOnScreen = 160;

        const string k_workspaceFolderPathPreferenceKey = "amberline.workspaceFolderPath";

        void Awake()
        {
            _resolvedWorkspaceFolderPath = ResolveWorkspaceFolderPath();
        }

        // Three sources, in this order: the field in the inspector, the folder /cd was last pointed
        // at, and finally the folder that contains the Unity project - the most useful default
        // while developing, because the agent can see the project it is running inside.
        //
        // THE FIELD COMES FIRST, and that is a change: the remembered folder used to win. It meant
        // that one /cd, months ago, silently decided where every later session started - the agent
        // opened in a folder from another project and nothing on screen explained why. A path typed
        // into the inspector is an explicit decision about this scene and should not be quietly
        // overridden by a preference nobody remembers setting. Leave the field empty and the
        // remembered folder is honoured again, which is what someone who lives in /cd wants.
        string ResolveWorkspaceFolderPath()
        {
            if (!string.IsNullOrWhiteSpace(_workspaceFolderPath) && Directory.Exists(_workspaceFolderPath))
                return Path.GetFullPath(_workspaceFolderPath);

            string rememberedFolderPath = ReadWorkspaceFolderPathRememberedFromLastSession();
            if (!string.IsNullOrEmpty(rememberedFolderPath))
                return rememberedFolderPath;

            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        string ReadWorkspaceFolderPathRememberedFromLastSession()
        {
            if (!_shouldRememberTheWorkspaceFolderBetweenSessions) return string.Empty;

            string rememberedFolderPath = PlayerPrefs.GetString(k_workspaceFolderPathPreferenceKey, string.Empty);

            // A folder that has been deleted or renamed since then falls straight through to the
            // fallbacks rather than starting the session with a sandbox that is already closed.
            return Directory.Exists(rememberedFolderPath) ? rememberedFolderPath : string.Empty;
        }

        void OnEnable()
        {
            _commandInputView.OnCommandSubmitted += OnCommandSubmitted;
            _commandInputView.OnCancelRequested += OnCancelRequested;

            SubscribeToAgentEvents();
            SubscribeToApprovalEvents();
        }

        void OnDisable()
        {
            _commandInputView.OnCommandSubmitted -= OnCommandSubmitted;
            _commandInputView.OnCancelRequested -= OnCancelRequested;

            UnsubscribeFromAgentEvents();
            UnsubscribeFromApprovalEvents();

            CancelCurrentTurn();
        }

        // The agent raises; this controller renders. Nothing goes the other way - the core has no
        // idea a terminal exists, which is what lets the same run drive a different front end.
        void SubscribeToAgentEvents()
        {
            if (_agentRunner == null) return;

            _agentRunner.Events.OnRunStarted += HandleRunStarted;
            _agentRunner.Events.OnAnswerTextStreaming += HandleAnswerTextStreaming;
            _agentRunner.Events.OnThoughtProduced += HandleThoughtProduced;
            _agentRunner.Events.OnToolCallStarting += HandleToolCallStarting;
            _agentRunner.Events.OnToolResultProduced += HandleToolResultProduced;
            _agentRunner.Events.OnCommandOutputLineProduced += HandleCommandOutputLineProduced;
            _agentRunner.Events.OnNoticeProduced += HandleNoticeProduced;
            _agentRunner.Events.OnRunFinished += HandleRunFinished;
        }

        void UnsubscribeFromAgentEvents()
        {
            if (_agentRunner == null) return;

            _agentRunner.Events.OnRunStarted -= HandleRunStarted;
            _agentRunner.Events.OnAnswerTextStreaming -= HandleAnswerTextStreaming;
            _agentRunner.Events.OnThoughtProduced -= HandleThoughtProduced;
            _agentRunner.Events.OnToolCallStarting -= HandleToolCallStarting;
            _agentRunner.Events.OnToolResultProduced -= HandleToolResultProduced;
            _agentRunner.Events.OnCommandOutputLineProduced -= HandleCommandOutputLineProduced;
            _agentRunner.Events.OnNoticeProduced -= HandleNoticeProduced;
            _agentRunner.Events.OnRunFinished -= HandleRunFinished;
        }

        // Three wires, one round trip: the gate asks, the card answers, and the gate says when the
        // question is over for any reason - including the user pressing Escape, which no card can
        // report because no card was answered.
        void SubscribeToApprovalEvents()
        {
            if (_agentRunner == null || _approvalCardView == null) return;

            _agentRunner.ApprovalGate.OnApprovalRequested += HandleApprovalRequested;
            _agentRunner.ApprovalGate.OnApprovalResolved += HandleApprovalResolved;
            _approvalCardView.OnApprovalAnswered += HandleApprovalAnswered;
        }

        void UnsubscribeFromApprovalEvents()
        {
            if (_agentRunner == null || _approvalCardView == null) return;

            _agentRunner.ApprovalGate.OnApprovalRequested -= HandleApprovalRequested;
            _agentRunner.ApprovalGate.OnApprovalResolved -= HandleApprovalResolved;
            _approvalCardView.OnApprovalAnswered -= HandleApprovalAnswered;
        }

        void Start()
        {
            // The controller owns the workspace decision and the sandbox obeys it, so the folder
            // shown in the top bar is provably the folder the tools are locked to.
            if (_agentRunner != null)
                _agentRunner.SetWorkspaceFolderPath(_resolvedWorkspaceFolderPath);

            _topBarView.SetWorkspaceFolderPath(_resolvedWorkspaceFolderPath);
            _statusBarView.SetWorkspacePath(_resolvedWorkspaceFolderPath);
            ShowStateWithContextUsage(k_idleStateText);
            _commandInputView.SetAvailableCommandsForAutocomplete(k_slashCommands);

            RunBootSequenceAsync().Forget();
        }

        // Boot text is typed out rather than printed, which is the whole point of the effect -
        // but nobody wants to sit through it on the fiftieth run, so any key skips ahead.
        void Update()
        {
            if (!_isBootSequenceRunning) return;
            if (Keyboard.current == null) return;

            if (Keyboard.current.anyKey.wasPressedThisFrame)
                _terminalView.RequestSkipOfCurrentAnimation();
        }

        async UniTaskVoid RunBootSequenceAsync()
        {
            _isBootSequenceRunning = true;
            _commandInputView.SetInputLocked(true);

            var cancellationToken = this.GetCancellationTokenOnDestroy();

            try
            {
                await _terminalView.AppendLineAsync("AMBERLINE v0.1 — a local coding agent", TerminalLineKind.Banner, cancellationToken, k_bootLineCharacterDelaySeconds);

                foreach (var bootLine in BuildBootLines())
                    await _terminalView.AppendLineAsync(bootLine, TerminalLineKind.Boot, cancellationToken, k_bootLineCharacterDelaySeconds);

                _terminalView.AppendLineInstant("type /help to see what this terminal understands", TerminalLineKind.Notice);
            }
            finally
            {
                // Whatever happened above, the terminal must end up usable. Leaving the row
                // locked would mean the only way out is to exit Play mode.
                _isBootSequenceRunning = false;
                _commandInputView.SetInputLocked(false);
            }
        }

        IEnumerable<string> BuildBootLines()
        {
            yield return "terminal ....... online";
            yield return "runtime ........ llama.cpp via LlamaLib 2.0.2";
            yield return "model .......... loading in the background";
            yield return $"workspace ...... {_resolvedWorkspaceFolderPath}";
            yield return "sandbox ........ on — the agent cannot read or write outside the workspace";
            yield return "commands ....... every shell command needs your approval";
        }

        void OnCommandSubmitted(string submittedCommand)
        {
            if (_isTurnInProgress) return;

            HandleSubmittedCommandAsync(submittedCommand).Forget();
        }

        async UniTaskVoid HandleSubmittedCommandAsync(string submittedCommand)
        {
            // Submitting is a statement that the user is done reading whatever they scrolled up to
            // look at. Everything from here on - the echo, the thought, the answer - is written at
            // the bottom of the log, so that is where the view has to be.
            if (_autoScrollView != null)
                _autoScrollView.PinToBottom();

            _isTurnInProgress = true;
            _stateTextToShowWhenTheTurnEnds = k_idleStateText;
            _commandInputView.SetInputLocked(true);
            ShowStateWithContextUsage(k_workingStateText);

            ReplaceCurrentTurnCancellationSource();

            try
            {
                _terminalView.AppendLineInstant($"> {submittedCommand}", TerminalLineKind.UserCommand);

                if (await TryHandleSlashCommandAsync(submittedCommand))
                    return;

                await RunAgentTurnAsync(submittedCommand, _currentTurnCancellationSource.Token);
            }
            catch (OperationCanceledException)
            {
                // The agent reports a cancel as a result rather than an exception, so this only
                // catches a cancel that landed outside the turn itself.
                _terminalView.AppendLineInstant(k_cancelledNoticeText, TerminalLineKind.Notice);
            }
            catch (Exception exception)
            {
                // One unhandled throw must not take the terminal down with it. Show the failure
                // as a line and carry on - the user can always type the next command.
                _terminalView.AppendLineInstant($"{exception.GetType().Name}: {exception.Message}", TerminalLineKind.Error);
                Debug.LogException(exception);
            }
            finally
            {
                _isTurnInProgress = false;
                _commandInputView.SetInputLocked(false);

                ShowStateWithContextUsage(_stateTextToShowWhenTheTurnEnds);
            }
        }

        // The state word and the context budget share the one label the status bar exposes. The
        // budget is only shown once the model has reported a window - before that it would read
        // "0/0", which looks like a bug rather than a model that is still loading.
        void ShowStateWithContextUsage(string stateText)
        {
            if (_statusBarView == null) return;

            if (_agentRunner == null)
            {
                _statusBarView.SetState(stateText);
                return;
            }

            ContextUsage contextUsage = _agentRunner.GetContextUsage();
            if (contextUsage.MaxTokens <= 0)
            {
                _statusBarView.SetState(stateText);
                return;
            }

            int fillPercentage = Mathf.RoundToInt(contextUsage.FillRatio * 100f);
            _statusBarView.SetState($"{stateText}  ctx {contextUsage.UsedTokens}/{contextUsage.MaxTokens} ({fillPercentage}%)");
        }

        void ReplaceCurrentTurnCancellationSource()
        {
            _currentTurnCancellationSource?.Dispose();
            _currentTurnCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
        }

        // Returns true when the input was a slash command and has been fully handled here,
        // so the caller knows not to hand it to the agent as well.
        //
        // Asynchronous for the sake of one command: /compact runs a real generation, and returning
        // before it finished would unlock the input row over a model call still in flight - and the
        // next turn would then be refused, because this model has exactly one slot.
        async UniTask<bool> TryHandleSlashCommandAsync(string submittedCommand)
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
            _terminalView.AppendLineInstant(_resolvedWorkspaceFolderPath, TerminalLineKind.Notice);
        }

        // The workspace can be moved mid-session because the agent is not tied to this Unity
        // project - Unity is only the host, and the folder it works in is the user's to choose.
        // The path has to be absolute: a relative one would be read against the very folder that
        // is being replaced, which is the one thing nobody can reason about while typing it.
        void ChangeWorkspaceFolder(string submittedCommand)
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent is wired up, so there is no workspace to move.", TerminalLineKind.Error);
                return;
            }

            // Belt and braces. A slash command cannot land mid-turn - the input row is locked for
            // the whole of one - but an open approval card belongs to the sandbox that is about to
            // be thrown away, and the call behind it is still waiting for an answer.
            if (_agentRunner.ApprovalGate.IsWaitingForTheUser)
            {
                _terminalView.AppendLineInstant("cd: answer the approval card that is open first, or press esc.", TerminalLineKind.Error);
                return;
            }

            string requestedFolderPath = ReadArgumentAfterTheCommandName(submittedCommand);

            if (string.IsNullOrEmpty(requestedFolderPath))
            {
                _terminalView.AppendLineInstant("cd: usage: /cd <absolute path>", TerminalLineKind.Notice);
                _terminalView.AppendLineInstant($"the workspace is {_resolvedWorkspaceFolderPath}", TerminalLineKind.Notice);
                return;
            }

            if (!TryResolveRequestedWorkspaceFolderPath(requestedFolderPath, out string fullFolderPath, out string failureReason))
            {
                _terminalView.AppendLineInstant($"cd: {failureReason}", TerminalLineKind.Error);
                return;
            }

            ApplyWorkspaceFolderPath(fullFolderPath);
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

        // Everything that was true of the old folder stops being true here. The sandbox moves, every
        // tool is rebuilt around it, and the conversation goes with them: it is a transcript about a
        // project the agent can no longer see, and keeping it would have the model confidently name
        // files that are not there. The screen is deliberately NOT cleared - the log is the record of
        // the session, and the line that moved the workspace belongs in it.
        void ApplyWorkspaceFolderPath(string fullFolderPath)
        {
            if (string.Equals(fullFolderPath, _resolvedWorkspaceFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                _terminalView.AppendLineInstant($"cd: already working in {fullFolderPath}", TerminalLineKind.Notice);
                return;
            }

            _agentRunner.SetWorkspaceFolderPath(fullFolderPath);
            _agentRunner.ClearConversation();

            // Read back rather than assumed: SetWorkspaceFolderPath keeps the previous folder if the
            // new one stopped existing between the check above and the call, and the top bar has to
            // go on naming the folder the tools are provably locked to.
            _resolvedWorkspaceFolderPath = _agentRunner.WorkspaceFolderPath;

            _topBarView.SetWorkspaceFolderPath(_resolvedWorkspaceFolderPath);
            _statusBarView.SetWorkspacePath(_resolvedWorkspaceFolderPath);
            ShowStateWithContextUsage(k_idleStateText);

            SaveWorkspaceFolderPathForNextSession(_resolvedWorkspaceFolderPath);

            _terminalView.AppendLineInstant($"workspace: {_resolvedWorkspaceFolderPath}", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("the conversation and every approval given this session were cleared with it.", TerminalLineKind.Notice);
            _terminalView.AppendLineInstant("/undo cannot reach back into the folder you just left.", TerminalLineKind.Notice);
        }

        void SaveWorkspaceFolderPathForNextSession(string fullFolderPath)
        {
            if (!_shouldRememberTheWorkspaceFolderBetweenSessions) return;

            PlayerPrefs.SetString(k_workspaceFolderPathPreferenceKey, fullFolderPath);
            PlayerPrefs.Save();
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
                await _agentRunner.CompactConversationAsync(GetCancellationTokenOfCurrentTurn());

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
            FinishTheThoughtAndTheRunningToolCard();

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

        async UniTask RunAgentTurnAsync(string submittedCommand, CancellationToken cancellationToken)
        {
            if (_agentRunner == null)
            {
                _terminalView.AppendLineInstant("no agent runner is assigned in the scene, so nothing can answer.", TerminalLineKind.Error);
                return;
            }

            _firstAnswerTextSignal = new UniTaskCompletionSource<bool>();

            var turnTask = RunAgentTurnAndReleaseTheSpinnerAsync(submittedCommand, cancellationToken);

            // The spinner only has to cover the silence before the first token - once text is on
            // screen the text is its own progress indicator. It waits on the signal rather than on
            // the cancellation token, because the turn reports a cancel as a value and never
            // throws, so the signal is the one thing guaranteed to be released either way.
            await _spinnerView.ShowUntilAsync(_firstAnswerTextSignal.Task, CancellationToken.None);

            await turnTask;
        }

        // Wraps the runner so the spinner is released the moment the first text lands - and also
        // when the turn ends without producing any text at all, which would otherwise leave the
        // spinner turning over an answer that is never coming.
        async UniTask<RunReport> RunAgentTurnAndReleaseTheSpinnerAsync(string submittedCommand, CancellationToken cancellationToken)
        {
            try
            {
                return await _agentRunner.RunTaskAsync(submittedCommand, cancellationToken);
            }
            finally
            {
                _firstAnswerTextSignal?.TrySetResult(true);
            }
        }

        void HandleRunStarted(string userTaskText)
        {
            // A fresh run never writes into the line the previous one left behind.
            FinishTheThoughtAndTheRunningToolCard();
        }

        // Called wherever a run can end, including the paths where it ends badly: a cancel, a
        // failure, a /clear. Anything still spinning at that point would spin forever, because the
        // event that was going to stop it is never coming.
        void FinishTheThoughtAndTheRunningToolCard()
        {
            CollapseTheThoughtOfThisIteration();
            StopTheRunningToolCardWith(string.Empty);
        }

        // Opened either on the first token of a plan, or as soon as a tool result comes back and
        // the model starts working out what to do next. The second case is why this is a method:
        // on a local model that gap is several seconds long, and before the block covered it the
        // terminal sat completely still in the middle of a run that was very much still going.
        //
        // An iteration that then writes no plan costs nothing - CollapseTheThoughtOfThisIteration
        // takes the empty line back out of the log.
        void OpenTheThoughtBlockIfThereIsNotOneOpen()
        {
            if (_currentThinkingBlock != null) return;

            _currentThinkingBlock = new ThinkingBlock(
                _terminalView, _terminalView.BeginStreamingLine(TerminalLineKind.Thinking));
        }

        void CollapseTheThoughtOfThisIteration()
        {
            if (_currentThinkingBlock == null) return;

            _currentThinkingBlock.Collapse();

            // An iteration that went straight to a tool call wrote no plan, and a folded block over
            // nothing is just an empty card.
            _currentThinkingBlock.RemoveIfThereWasNoThought();
            _currentThinkingBlock = null;
        }

        void StopTheRunningToolCardWith(string outcomeSuffix)
        {
            if (!_runningToolSpinner.IsRunning) return;

            _runningToolSpinner.Stop(_textOfTheRunningToolCard + outcomeSuffix);
            _textOfTheRunningToolCard = string.Empty;
        }

        // The model's plan, arriving a token at a time. The text itself never reaches the screen -
        // it goes into a folded block that spins while the plan is being written and opens on a
        // click. The agent narrating itself is not the answer, and printing every word of it in
        // full buried the tool calls that are.
        void HandleAnswerTextStreaming(string cumulativeText)
        {
            if (string.IsNullOrEmpty(cumulativeText)) return;

            OpenTheThoughtBlockIfThereIsNotOneOpen();

            // The loop hands over the whole text so far, never a delta - assign, never append.
            _currentThinkingBlock.SetFullText(cumulativeText);

            _firstAnswerTextSignal?.TrySetResult(true);
        }

        void HandleThoughtProduced(string thoughtText)
        {
            if (!string.IsNullOrEmpty(thoughtText))
                HandleAnswerTextStreaming(thoughtText);

            // Folded and closed off, so the next iteration's plan starts on a line of its own
            // instead of reopening this one.
            CollapseTheThoughtOfThisIteration();
        }

        // The card is opened here and finished in HandleToolResultProduced, and it spins for
        // everything in between - the approval card the user has not answered yet, a whole
        // `dotnet build`, a grep across the project. That gap used to be the one place in the
        // product where the terminal showed nothing at all while something was clearly happening.
        void HandleToolCallStarting(ToolCall toolCall, ToolCallParseStage parseStage)
        {
            CollapseTheThoughtOfThisIteration();

            _textOfTheRunningToolCard = BuildToolCardText(toolCall, parseStage);
            _runningToolSpinner.Start(_terminalView.BeginStreamingLine(TerminalLineKind.ToolActivity),
                _textOfTheRunningToolCard);

            _firstAnswerTextSignal?.TrySetResult(true);
        }

        string BuildToolCardText(ToolCall toolCall, ToolCallParseStage parseStage)
        {
            string cardText = $"{toolCall.ToolName}  {DescribeKeyArgumentOfCall(toolCall)}";

            // A call that only survived repair is worth seeing. Silently rendering it exactly like
            // a clean one would hide the single most useful signal about how the model is coping.
            if (parseStage != ToolCallParseStage.DirectExtraction)
                cardText += $"  [{parseStage}]";

            return cardText;
        }

        string DescribeKeyArgumentOfCall(ToolCall toolCall)
        {
            foreach (string argumentName in k_argumentNamesWorthShowingOnACard)
            {
                string argumentValue = toolCall.GetArgument(argumentName);
                if (!string.IsNullOrEmpty(argumentValue))
                    return ShortenForOneLine(argumentValue, k_maximumCharactersOfAToolArgumentOnScreen);
            }

            return string.Empty;
        }

        // The gate has parked the whole run on an answer and raised this on the main thread. What
        // the call would actually do still has to be worked out - a file read and a diff - so the
        // card is built asynchronously and this returns at once, which is what keeps Escape live
        // while the user is reading.
        void HandleApprovalRequested(ApprovalRequest approvalRequest)
        {
            _approvalRequestWaitingForACard = approvalRequest;
            ShowCardForApprovalRequestAsync(approvalRequest).Forget();
        }

        async UniTaskVoid ShowCardForApprovalRequestAsync(ApprovalRequest approvalRequest)
        {
            var fileChangePreview = await BuildFileChangePreviewForRequestAsync(approvalRequest);

            // The run can be cancelled while the preview is being worked out. The gate has then
            // already resolved this request, so a card for it would sit on screen forever with
            // nothing behind it waiting for the answer.
            if (!ReferenceEquals(_approvalRequestWaitingForACard, approvalRequest)) return;

            if (fileChangePreview == null)
            {
                _approvalCardView.ShowRequest(approvalRequest);
                return;
            }

            if (!fileChangePreview.IsAvailable)
            {
                LetTheCallRunSoTheModelReadsWhyItFailed(approvalRequest, fileChangePreview);
                return;
            }

            _approvalCardView.ShowRequest(approvalRequest, fileChangePreview.Diff);
        }

        // Null means "this tool cannot describe its own change" - a command, or a tool with no
        // preview at all - and the card then falls back to the payload the model asked for.
        async UniTask<FileChangePreview> BuildFileChangePreviewForRequestAsync(ApprovalRequest approvalRequest)
        {
            var toolExecutor = _agentRunner.FindExecutorForToolName(approvalRequest.ToolName);

            if (!(toolExecutor is IFileChangePreviewProvider fileChangePreviewProvider)) return null;
            if (approvalRequest.Call == null) return null;

            try
            {
                return await fileChangePreviewProvider.BuildFileChangePreviewAsync(approvalRequest.Call,
                    GetCancellationTokenOfCurrentTurn());
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                // A preview that throws must not take the approval down with it: the card can still
                // be shown with the payload, and the user can still answer for it.
                Debug.LogWarning($"[TerminalCliController] The {approvalRequest.ToolName} preview threw: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        CancellationToken GetCancellationTokenOfCurrentTurn()
        {
            return _currentTurnCancellationSource == null
                ? this.GetCancellationTokenOnDestroy()
                : _currentTurnCancellationSource.Token;
        }

        // There is nothing to approve: the path is refused, the anchor matched nothing, the file
        // already holds exactly that text. Showing a card would ask the user to authorise a change
        // that cannot happen. So the call is let through instead - the executor works the same
        // answer out again and hands the model the sentence that gets it unstuck.
        void LetTheCallRunSoTheModelReadsWhyItFailed(ApprovalRequest approvalRequest, FileChangePreview fileChangePreview)
        {
            _terminalView.AppendLineInstant(
                $"  nothing to approve — {approvalRequest.ToolName} cannot be applied, so it was let through to report why",
                TerminalLineKind.Notice);

            _agentRunner.ApprovalGate.SubmitDecision(new ApprovalDecision(true, false));
        }

        // Raised for every way a request stops being pending, answered or not. Closing the card
        // here is what retires it when the user cancels the turn instead of answering.
        void HandleApprovalResolved()
        {
            _approvalRequestWaitingForACard = null;
            _approvalCardView.CloseCard();
        }

        void HandleApprovalAnswered(ApprovalDecision approvalDecision)
        {
            _agentRunner.ApprovalGate.SubmitDecision(approvalDecision);
        }

        // A running command, printing. This is the only place in the product where the terminal
        // shows something while a tool is still working, and it is the reason a build does not look
        // like a hang: the compiler's own output arrives line by line, exactly as it would in a real
        // shell. Blank lines are dropped, because the log holds only 400 lines and a build spends a
        // good many of them on nothing.
        void HandleCommandOutputLineProduced(string outputLine)
        {
            if (string.IsNullOrWhiteSpace(outputLine)) return;

            _terminalView.AppendLineInstant("  " + ShortenForOneLine(outputLine, k_maximumCharactersOfACommandOutputLineOnScreen),
                TerminalLineKind.ToolActivity);

            _firstAnswerTextSignal?.TrySetResult(true);
        }

        // Something the loop did on its own account - today, compacting the transcript. Shown so a
        // pause of ten seconds in the middle of a run has a name.
        void HandleNoticeProduced(string noticeText)
        {
            if (string.IsNullOrWhiteSpace(noticeText)) return;

            _terminalView.AppendLineInstant(noticeText, TerminalLineKind.Notice);
            CollapseTheThoughtOfThisIteration();
        }

        // A failed tool is not a failed run - the model reads the error and tries something else -
        // so it is rendered red and the turn carries on.
        void HandleToolResultProduced(ToolCall toolCall, ToolResult toolResult)
        {
            StopTheRunningToolCardWith(toolResult.IsSuccess ? k_toolCardSuffixWhenItWorked : k_toolCardSuffixWhenItFailed);

            // finish hands its summary straight back, and the run report prints it as the answer a
            // moment later. Showing it here as well put the same sentence on screen twice.
            if (toolResult.IsSuccess && toolCall.ToolName == ToolRegistry.k_finishToolName) return;

            var lineKind = toolResult.IsSuccess ? TerminalLineKind.ToolActivity : TerminalLineKind.Error;
            _terminalView.AppendLineInstant(BuildShortResultText(toolResult), lineKind);

            AppendDiffOfTheFileChangeThatLanded(toolResult);

            // The model is about to read this result and decide what to do next, and that decision
            // is the longest silent stretch of a run. Opened now, the block spins through it.
            OpenTheThoughtBlockIfThereIsNotOneOpen();
        }

        // Shown after EVERY write that lands, in every permission mode. The point of an auto-approve
        // mode is that the user stops being asked, not that they stop being told - before this, a
        // whole session of edits read as a column of "+3 -1" and nothing else. In ask-every-time the
        // card showed a preview of what was ABOUT to happen; this is the record of what did.
        void AppendDiffOfTheFileChangeThatLanded(ToolResult toolResult)
        {
            if (!toolResult.IsSuccess || toolResult.AppliedFileDiff == null) return;

            if (_diffView == null)
            {
                Debug.LogWarning("[TerminalCliController] No diff view is assigned, so the change was only reported as a line.");
                return;
            }

            _terminalView.AppendElementToLog(
                _diffView.BuildDiffElement(toolResult.ChangedFileDisplayPath, toolResult.AppliedFileDiff.Lines));
        }

        // The model gets the whole output; the log gets its first line. Dumping two hundred lines
        // of a file into the terminal would bury the run it is meant to make readable.
        string BuildShortResultText(ToolResult toolResult)
        {
            string[] outputLines = toolResult.Output.Split('\n');
            string shortText = "  " + ShortenForOneLine(outputLines[0], k_maximumCharactersOfAToolResultOnScreen);

            if (outputLines.Length > 1)
                shortText += $"  [+{outputLines.Length - 1} more lines]";

            return shortText;
        }

        static string ShortenForOneLine(string text, int maximumCharacterCount)
        {
            string singleLineText = text.Replace('\n', ' ').Replace('\r', ' ').Trim();

            return singleLineText.Length <= maximumCharacterCount
                ? singleLineText
                : singleLineText.Substring(0, maximumCharacterCount) + "...";
        }

        // Fires exactly once per run, on every path the loop can take. Everything the user sees at
        // the end of a turn is decided here.
        void HandleRunFinished(RunReport runReport)
        {
            FinishTheThoughtAndTheRunningToolCard();

            if (runReport.DidFinishCleanly)
            {
                _terminalView.AppendLineInstant(runReport.SummaryText, TerminalLineKind.AgentMessage);
                return;
            }

            if (runReport.WasCancelled)
            {
                // Whatever arrived before the cancel stays on screen: half an answer is still an
                // answer, and deleting it would throw away work the user already paid for.
                _terminalView.AppendLineInstant(k_cancelledNoticeText, TerminalLineKind.Notice);
                _stateTextToShowWhenTheTurnEnds = k_cancelledStateText;
                return;
            }

            _terminalView.AppendLineInstant(runReport.SummaryText, TerminalLineKind.Error);
        }

        void OnCancelRequested()
        {
            CancelCurrentTurn();
        }

        void CancelCurrentTurn()
        {
            if (_currentTurnCancellationSource == null) return;
            if (_currentTurnCancellationSource.IsCancellationRequested) return;

            _currentTurnCancellationSource.Cancel();
        }
    }
}
