using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    // The composition root of the agent side. It builds the pieces, holds them for the lifetime of
    // the scene, and hands the terminal three things: a way to run a task, a way to read the
    // context budget, and the event stream to render the run from.
    //
    // Dependencies run one way only: the UI calls in here and nothing in here ever references a
    // type from Amberline.Ui.
    //
    // Execution order 100, because LLMClient.Start is async void and carries no execution-order
    // attribute of its own, so this must not start asking the model things before it exists.
    [DefaultExecutionOrder(100)]
    public class AgentRunner : MonoBehaviour
    {
        [Header("Model")]
        [SerializeField] LlmGateway _llmGateway;

        // Created as fields rather than in Awake so a subscriber can attach in its own OnEnable
        // without depending on which object Unity woke up first.
        readonly AgentEvents _agentEvents = new AgentEvents();
        readonly ApprovalGate _approvalGate = new ApprovalGate();

        ContextManager _contextManager;
        ChatTemplateRenderer _chatTemplateRenderer;
        PathSandbox _pathSandbox;
        FileWriteService _fileWriteService;
        ToolRegistry _toolRegistry;
        ToolRunner _toolRunner;
        AgentLoop _agentLoop;

        string _workspaceFolderPath = string.Empty;

        // The listing of the workspace that is pinned underneath the system prompt. Worked out once
        // per folder, because the pinned block has to stay byte-identical for the whole session.
        string _workspaceBriefText = string.Empty;

        // Pinning the system prompt is async, and several callers want it done: Start kicks it
        // off, /cd re-pins with a new workspace brief, and the first turn the user submits waits
        // for whichever of those is in flight. Held as ONE task so they share the work instead of
        // each inserting a system block of its own.
        //
        // A System.Threading.Tasks.Task and not a UniTask, and that is not an oversight. UniTask's
        // Preserve() memoises a RESULT: a second awaiter arriving while the first is still waiting
        // throws "Already continuation registered, can not await twice". On a cold model this
        // waits for the weights to load, which is tens of seconds of overlap - and a run started
        // in that window died on that exception before it reached the model. Task allows any
        // number of concurrent awaiters, which is exactly what this needs.
        Task _pinningOfTheSystemPrompt;

        /// <summary>Everything that happens inside a run, for the terminal to render.</summary>
        public AgentEvents Events => _agentEvents;

        /// <summary>
        /// The handshake the terminal draws its approval card from. Exposed rather than wrapped,
        /// because the front end both subscribes to it and answers it, and a wrapper would only
        /// repeat its whole surface.
        /// </summary>
        public ApprovalGate ApprovalGate => _approvalGate;

        /// <summary>The folder every tool is sandboxed to.</summary>
        public string WorkspaceFolderPath => _workspaceFolderPath;

        void Awake()
        {
            _contextManager = new ContextManager(BuildTokenCounterFromTheModel());
            _chatTemplateRenderer = new ChatTemplateRenderer(BuildChatTemplateApplierFromTheModel());
            BuildToolStackForWorkspaceFolder(ResolveFallbackWorkspaceFolderPath());
        }

        // The loaded model's own chat template, so the transcript is rendered in the envelope that
        // model was trained on rather than in one format this project picked. Null when there is no
        // gateway, and the renderer then falls back to ChatML.
        Func<IReadOnlyList<ChatMessage>, string> BuildChatTemplateApplierFromTheModel()
        {
            if (_llmGateway == null)
            {
                return null;
            }

            return _llmGateway.ApplyTheModelsOwnChatTemplate;
        }

        // The model's own tokenizer, so the status bar reports real token counts instead of the
        // character-length estimate ContextManager falls back to when this is null.
        Func<string, UniTask<int>> BuildTokenCounterFromTheModel()
        {
            if (_llmGateway == null)
            {
                Debug.LogError("[AgentRunner] No LlmGateway is assigned, so this runner cannot answer anything.");
                return null;
            }

            return _llmGateway.CountTokensAsync;
        }

        // The folder that contains the Unity project. Only a stand-in until the terminal reports the
        // workspace the user actually granted, so the sandbox is never left open in the meantime.
        static string ResolveFallbackWorkspaceFolderPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        void Start()
        {
            PinSystemPromptWhenTheModelIsReadyAsync().Forget();
        }

        /// <summary>
        /// Points the sandbox and every tool at <paramref name="workspaceFolderPath"/>. The terminal
        /// calls this once it has resolved the folder; an unusable path is ignored with a warning
        /// rather than silently closing the sandbox mid-session.
        /// </summary>
        public void SetWorkspaceFolderPath(string workspaceFolderPath)
        {
            if (string.IsNullOrWhiteSpace(workspaceFolderPath) || !Directory.Exists(workspaceFolderPath))
            {
                Debug.LogWarning($"[AgentRunner] The workspace folder '{workspaceFolderPath}' does not exist, so the previous one was kept.");
                return;
            }

            string fullWorkspaceFolderPath = Path.GetFullPath(workspaceFolderPath);
            if (string.Equals(fullWorkspaceFolderPath, _workspaceFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            BuildToolStackForWorkspaceFolder(fullWorkspaceFolderPath);
        }

        // Everything below the sandbox is rebuilt with it, because every executor holds the sandbox
        // it was constructed with. Rebuilding is cheap and happens at most once per session today.
        void BuildToolStackForWorkspaceFolder(string workspaceFolderPath)
        {
            _workspaceFolderPath = workspaceFolderPath;
            _pathSandbox = new PathSandbox(workspaceFolderPath);

            // Read off disk once, here, and never again for this folder. The sandbox is passed in
            // so the listing hides exactly what the tools would refuse to open.
            _workspaceBriefText = WorkspaceBriefText.BuildBriefForWorkspaceFolder(workspaceFolderPath, _pathSandbox);
            LetTheSystemPromptBePinnedAgainWithTheNewBrief();

            // Built here and nowhere else, because its constructor reads
            // Application.persistentDataPath, which throws off the main thread - and every caller
            // below this point runs on the thread pool.
            _fileWriteService = new FileWriteService(_pathSandbox);
            _approvalGate.SetPathSandbox(_pathSandbox);

            _toolRegistry = new ToolRegistry();
            _toolRegistry.RegisterExecutor(new ReadFileTool(_pathSandbox));
            _toolRegistry.RegisterExecutor(new ListDirTool(_pathSandbox));
            _toolRegistry.RegisterExecutor(new GrepTool(_pathSandbox));
            _toolRegistry.RegisterExecutor(new FindFileTool(_pathSandbox));
            _toolRegistry.RegisterExecutor(new WriteFileTool(_pathSandbox, _fileWriteService));
            _toolRegistry.RegisterExecutor(new EditFileTool(_pathSandbox, _fileWriteService));
            _toolRegistry.RegisterExecutor(new RunCommandTool(BuildCommandRunnerForWorkspaceFolder(workspaceFolderPath)));
            _toolRegistry.RegisterExecutor(new FinishTool());

            _toolRunner = new ToolRunner(_toolRegistry, _approvalGate.RequestApprovalAsync);
            _agentLoop = new AgentLoop(_llmGateway, _contextManager, _toolRegistry, _toolRunner, _agentEvents,
                _chatTemplateRenderer);
        }

        // A pinned block that still describes the PREVIOUS folder is worse than no block at all: it
        // hands the model a listing of files that are no longer reachable. /cd clears the
        // conversation as well, so nothing in the transcript is left pointing at the old folder.
        //
        // Nothing happens before the first pin, because there is no block to replace yet - Awake
        // builds the tool stack long before the tokenizer is up.
        void LetTheSystemPromptBePinnedAgainWithTheNewBrief()
        {
            if (_pinningOfTheSystemPrompt == null) return;

            _pinningOfTheSystemPrompt =
                PinTheSystemBlockAfterTheOneAlreadyRunningAsync(_pinningOfTheSystemPrompt, BuildPinnedSystemBlock()).AsTask();
        }

        // Chained rather than started alongside. Both writes land on the same slot at index 0, and
        // measuring a prompt with the model's tokenizer takes long enough that the first one could
        // otherwise finish LAST and put the folder the user just left back into the transcript.
        async UniTask PinTheSystemBlockAfterTheOneAlreadyRunningAsync(Task pinningAlreadyRunning, string pinnedSystemBlock)
        {
            await pinningAlreadyRunning;

            // Everything below waits on frames and calls the tokenizer, and neither is safe off the
            // player loop - awaiting a Task can resume anywhere.
            await UniTask.SwitchToMainThread();

            await _contextManager.SetPinnedSystemBlockAsync(pinnedSystemBlock);
        }

        // Built here, on the main thread, because it captures the synchronisation context it will
        // later post command output back through - and everything below this point runs on the
        // thread pool, where SynchronizationContext.Current is not the one the UI lives on.
        CommandRunner BuildCommandRunnerForWorkspaceFolder(string workspaceFolderPath)
        {
            return new CommandRunner(workspaceFolderPath, _agentEvents.RaiseCommandOutputLineProduced);
        }

        /// <summary>
        /// Runs one task from the user to its end: the ReAct loop thinks, calls tools and stops when
        /// it has an answer. Never throws - a cancel and a failure both come back as a report - and
        /// <see cref="Events"/> carries everything that happened on the way.
        /// </summary>
        public async UniTask<RunReport> RunTaskAsync(string userTaskText, CancellationToken cancellationToken)
        {
            // A rejection binds the run it was given in. Carrying it further would leave a user who
            // says "go on, do it" on the next turn unable to be asked at all.
            _approvalGate.ForgetCallsTheUserRejected();

            await EnsureSystemPromptIsPinnedAsync();
            return await _agentLoop.RunAsync(userTaskText, cancellationToken);
        }

        /// <summary>
        /// Backs /compact: folds the older part of the conversation into one summary message. The
        /// loop does this on its own once the transcript approaches the window; this is the same
        /// thing on demand. False means nothing was changed, and the conversation is exactly as it
        /// was - never half rewritten.
        /// </summary>
        public async UniTask<bool> CompactConversationAsync(CancellationToken cancellationToken)
        {
            return await _agentLoop.CompactTranscriptAsync(cancellationToken);
        }

        /// <summary>How full the model's context window is right now, for the status bar.</summary>
        public ContextUsage GetContextUsage()
        {
            int usableContextTokens = _llmGateway == null ? 0 : _llmGateway.UsableContextTokens;
            return _contextManager.GetContextUsage(usableContextTokens);
        }

        /// <summary>The tools the model can really call - the ones an executor exists for. Backs
        /// /tools, and it is the same list the grammar is built from.</summary>
        public IReadOnlyList<string> GetNamesOfCallableTools()
        {
            return _toolRegistry.GetNamesOfCallableTools();
        }

        /// <summary>
        /// The executor registered for <paramref name="toolName"/>, or null when there is none.
        /// The terminal uses it to ask a mutating tool what a pending call would do before the user
        /// answers for it.
        /// </summary>
        public IToolExecutor FindExecutorForToolName(string toolName)
        {
            return _toolRegistry.FindExecutorForToolName(toolName);
        }

        /// <summary>
        /// Backs /undo: puts the most recent file change the agent made back the way it was. The
        /// reason it comes back with is written for the USER, not for the model.
        /// </summary>
        public bool TryUndoLastFileChange(out string undoneDisplayPath, out string failureReason)
        {
            undoneDisplayPath = string.Empty;
            failureReason = null;

            if (_fileWriteService == null)
            {
                failureReason = "there is no file service, so nothing can be undone.";
                return false;
            }

            return _fileWriteService.TryUndoLastChange(out undoneDisplayPath, out failureReason);
        }

        /// <summary>
        /// Backs /clear: drops the conversation but keeps the pinned system block, because the
        /// next message starts a fresh run.
        /// </summary>
        public void ClearConversation()
        {
            _contextManager.Clear();

            // "Approve every write_file this session" was granted over a conversation that no
            // longer exists, so it must not carry into the one that replaces it. The same is true
            // of every no the user gave: both were answers about work that has been thrown away.
            _approvalGate.ForgetApprovalsRememberedForTheSession();
            _approvalGate.ForgetCallsTheUserRejected();
        }

        // Pinning measures the prompt with the model's tokenizer, which waits for the model to
        // load, so this doubles as the warm-up: the weights are already in memory by the time the
        // user has finished reading the boot text.
        async UniTaskVoid PinSystemPromptWhenTheModelIsReadyAsync()
        {
            if (_llmGateway == null)
            {
                return;
            }

            await EnsureSystemPromptIsPinnedAsync();
        }

        // Started at most once. Without the guard, a user who submits during the boot animation
        // would have two callers pass the "not pinned yet" check while the tokenizer was still
        // loading, and the transcript would end up with two system blocks.
        UniTask EnsureSystemPromptIsPinnedAsync()
        {
            if (_pinningOfTheSystemPrompt == null)
            {
                _pinningOfTheSystemPrompt = LearnHowTheModelWantsToBeTalkedToAndPinTheSystemBlockAsync().AsTask();
            }

            return _pinningOfTheSystemPrompt.AsUniTask();
        }

        // The envelope has to be known before the first prompt is rendered, and it can only be
        // asked for once the model is loaded - so it is learned here, on the same wait the pinning
        // already pays for. Whether the model has a real reasoning token is settled here too,
        // because the tokenizer is the only thing that can answer it and only this class holds one.
        async UniTask LearnHowTheModelWantsToBeTalkedToAndPinTheSystemBlockAsync()
        {
            if (_llmGateway != null && await _llmGateway.WaitUntilReadyAsync(CancellationToken.None))
            {
                int tokensInTheThinkTag = await _llmGateway.CountTokensAsync(ChatTemplateRenderer.ThinkBlockOpenTag);
                _chatTemplateRenderer.LearnTheFormatOfTheLoadedModel(tokensInTheThinkTag == 1);
            }

            await _contextManager.SetPinnedSystemBlockAsync(BuildPinnedSystemBlock());
        }

        // The fixed rules first, then the folder they apply to. Two halves, one block: the model
        // reads them as one message, and llama.cpp caches them as one prefix.
        string BuildPinnedSystemBlock()
        {
            if (string.IsNullOrEmpty(_workspaceBriefText))
            {
                return SystemPromptText.k_systemPrompt;
            }

            return SystemPromptText.k_systemPrompt + "\n\n" + _workspaceBriefText;
        }
    }
}
