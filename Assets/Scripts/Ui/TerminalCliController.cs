using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Amberline.Agent;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

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
        [Space]
        [SerializeField] TopBarView _topBarView;
        [SerializeField] StatusBarView _statusBarView;
        [Space]
        [Tooltip("Draws the card that stops the agent until the user answers. Without it a write can never be approved.")]
        [SerializeField] ApprovalCardView _approvalCardView;

        [Header("Agent")]
        [SerializeField] AgentRunner _agentRunner;

        [Header("Workspace")]
        [Tooltip("Folder the agent is allowed to work in. Left empty, it falls back to the folder that contains this Unity project.")]
        [SerializeField] string _workspaceFolderPath;

        string _resolvedWorkspaceFolderPath;

        // Cancels whatever the current turn is doing. Replaced at the start of every turn so a
        // cancel can never reach back and kill the turn after it.
        CancellationTokenSource _currentTurnCancellationSource;

        bool _isBootSequenceRunning;
        bool _isTurnInProgress;

        // The line the model's current plan is streaming into. Created on the first token rather
        // than up front, so an iteration that produces no plan leaves no empty box behind, and
        // cleared once the plan is final so the next iteration starts a line of its own.
        Label _streamingThoughtLabel;

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

        // Checked in this order, so grep shows what it searched for rather than the folder it
        // searched in, and read_file shows the file rather than nothing. "summary" is deliberately
        // absent: it belongs to finish, whose summary is the answer and is printed on its own.
        static readonly string[] k_argumentNamesWorthShowingOnACard = { "pattern", "command", "path" };

        static readonly string[] k_slashCommands =
            { "/help", "/cwd", "/context", "/tools", "/approve-mode", "/compact", "/undo", "/clear", "/exit" };

        const int k_maximumCharactersOfACommandOutputLineOnScreen = 160;

        void Awake()
        {
            _resolvedWorkspaceFolderPath = ResolveWorkspaceFolderPath();
        }

        // Falls back to the folder that contains the Unity project, which is the most useful
        // default while developing: the agent can see the project it is running inside.
        string ResolveWorkspaceFolderPath()
        {
            if (!string.IsNullOrWhiteSpace(_workspaceFolderPath) && Directory.Exists(_workspaceFolderPath))
                return Path.GetFullPath(_workspaceFolderPath);

            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
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

        // The phase matters as much as the list: the same four names mean something different once
        // the agent has been let past read-only, and this is the only place that difference shows.
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
            _terminalView.AppendLineInstant($"phase ..... {DescribeCurrentToolPhase()}", TerminalLineKind.Notice);
        }

        string DescribeCurrentToolPhase()
        {
            return _agentRunner.CurrentToolPhase == ToolPhase.Explore
                ? "explore - read only, writing unlocks after the first successful look at the project"
                : "edit - writing tools are unlocked, and every write still needs your approval";
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
            _streamingThoughtLabel = null;
        }

        // The model's plan, arriving a token at a time. It is shown dim, because it is the agent
        // narrating itself rather than answering - the answer is the finish summary at the end.
        void HandleAnswerTextStreaming(string cumulativeText)
        {
            if (string.IsNullOrEmpty(cumulativeText)) return;

            if (_streamingThoughtLabel == null)
                _streamingThoughtLabel = _terminalView.BeginStreamingLine(TerminalLineKind.Notice);

            // The loop hands over the whole text so far, never a delta - assign, never append.
            _streamingThoughtLabel.text = cumulativeText;

            _firstAnswerTextSignal?.TrySetResult(true);
        }

        void HandleThoughtProduced(string thoughtText)
        {
            if (!string.IsNullOrEmpty(thoughtText))
                HandleAnswerTextStreaming(thoughtText);

            // Closed off, so the next iteration's plan starts on a line of its own instead of
            // overwriting this one.
            _streamingThoughtLabel = null;
        }

        void HandleToolCallStarting(ToolCall toolCall, ToolCallParseStage parseStage)
        {
            _terminalView.AppendLineInstant(BuildToolCardText(toolCall, parseStage), TerminalLineKind.ToolActivity);
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
            _streamingThoughtLabel = null;
        }

        // A failed tool is not a failed run - the model reads the error and tries something else -
        // so it is rendered red and the turn carries on.
        void HandleToolResultProduced(ToolCall toolCall, ToolResult toolResult)
        {
            // finish hands its summary straight back, and the run report prints it as the answer a
            // moment later. Showing it here as well put the same sentence on screen twice.
            if (toolResult.IsSuccess && toolCall.ToolName == ToolRegistry.k_finishToolName) return;

            var lineKind = toolResult.IsSuccess ? TerminalLineKind.ToolActivity : TerminalLineKind.Error;
            _terminalView.AppendLineInstant(BuildShortResultText(toolResult), lineKind);
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
            _streamingThoughtLabel = null;

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
