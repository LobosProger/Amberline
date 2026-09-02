using System;
using Amberline.Agent;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// The three-wire handshake that stops the agent until the user answers: the gate asks, the
    /// card answers, and the gate says when the question is over for any reason - including Escape,
    /// which no card can report because no card was answered.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="TerminalCliController"/>. It subscribes and unsubscribes itself, so
    /// the controller's OnEnable and OnDisable each call one method instead of holding three more
    /// wires of their own.
    /// <para>
    /// The one field it owns, the request a card is being built for, is exactly why this is a
    /// class and not a set of loose methods: the build is asynchronous, and comparing against that
    /// field is what stops a card appearing for a question nobody is waiting on any more.
    /// </para>
    /// </remarks>
    public class ApprovalFlowPresenter
    {
        readonly TerminalView _terminalView;
        readonly ApprovalCardView _approvalCardView;
        readonly DiffView _diffView;
        readonly AgentRunner _agentRunner;
        readonly ITerminalCommandHost _host;

        // The request a card is being built for right now. The build is asynchronous, so by the time
        // it finishes the run may already have been cancelled and this request resolved - comparing
        // against it is what stops a card appearing for a question nobody is waiting on.
        ApprovalRequest _approvalRequestWaitingForACard;

        public ApprovalFlowPresenter(TerminalView terminalView, ApprovalCardView approvalCardView,
            DiffView diffView, AgentRunner agentRunner, ITerminalCommandHost host)
        {
            _terminalView = terminalView;
            _approvalCardView = approvalCardView;
            _diffView = diffView;
            _agentRunner = agentRunner;
            _host = host;
        }

        public void Subscribe()
        {
            if (_agentRunner == null || _approvalCardView == null) return;

            _agentRunner.ApprovalGate.OnApprovalRequested += HandleApprovalRequested;
            _agentRunner.ApprovalGate.OnApprovalResolved += HandleApprovalResolved;
            _approvalCardView.OnApprovalAnswered += HandleApprovalAnswered;
        }

        public void Unsubscribe()
        {
            if (_agentRunner == null || _approvalCardView == null) return;

            _agentRunner.ApprovalGate.OnApprovalRequested -= HandleApprovalRequested;
            _agentRunner.ApprovalGate.OnApprovalResolved -= HandleApprovalResolved;
            _approvalCardView.OnApprovalAnswered -= HandleApprovalAnswered;
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
                LetTheCallRunSoTheModelReadsWhyItFailed(approvalRequest);
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
                    _host.GetCancellationTokenOfCurrentTurn());
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                // A preview that throws must not take the approval down with it: the card can still
                // be shown with the payload, and the user can still answer for it.
                Debug.LogWarning($"[ApprovalFlowPresenter] The {approvalRequest.ToolName} preview threw: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        // There is nothing to approve: the path is refused, the anchor matched nothing, the file
        // already holds exactly that text. Showing a card would ask the user to authorise a change
        // that cannot happen. So the call is let through instead - the executor works the same
        // answer out again and hands the model the sentence that gets it unstuck.
        void LetTheCallRunSoTheModelReadsWhyItFailed(ApprovalRequest approvalRequest)
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

        /// <summary>
        /// Draws what a write actually changed, after it landed. Called by the controller from the
        /// tool-result handler, which is where the outcome of a call arrives.
        /// </summary>
        /// <remarks>
        /// Shown after EVERY write that lands, in every permission mode. The point of an
        /// auto-approve mode is that the user stops being asked, not that they stop being told -
        /// before this, a whole session of edits read as a column of "+3 -1" and nothing else. In
        /// ask-every-time the card showed a preview of what was ABOUT to happen; this is the record
        /// of what did.
        /// </remarks>
        public void AppendDiffOfTheFileChangeThatLanded(ToolResult toolResult)
        {
            if (!toolResult.IsSuccess || toolResult.AppliedFileDiff == null) return;

            if (_diffView == null)
            {
                Debug.LogWarning("[ApprovalFlowPresenter] No diff view is assigned, so the change was only reported as a line.");
                return;
            }

            VisualElement diffElement =
                _diffView.BuildDiffElement(toolResult.ChangedFileDisplayPath, toolResult.AppliedFileDiff.Lines);

            _terminalView.AppendElementToLog(diffElement);
        }
    }
}
