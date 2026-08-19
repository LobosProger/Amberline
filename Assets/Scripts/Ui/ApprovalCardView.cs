using System;
using Amberline.Agent;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Draws the pending approval as a card inside the terminal log and captures the answer.
    /// Renders only - it never decides anything. <see cref="ApprovalGate"/> asks, this shows the
    /// question, and <see cref="OnApprovalAnswered"/> carries the reply back.
    /// <para>
    /// KEYBOARD FIRST. This is a CLI, so y approves, n rejects and a approves every call to that
    /// tool for the rest of the session. The chips are buttons as well, but the keys are the
    /// product: pointer input into a panel that draws into a render texture is only correctly
    /// mapped while <see cref="RenderTexturePointerMapper"/> is on the document, and the CRT pass
    /// warps the image slightly on top of that.
    /// </para>
    /// <para>
    /// A command card is red-bordered and carries an extra warning, and it never offers the a key.
    /// A shell command is the most dangerous thing the agent can ask for and it is asked about in
    /// every permission mode, so it must not look like an ordinary edit.
    /// </para>
    /// </summary>
    public class ApprovalCardView : MonoBehaviour
    {
        [Header("Templates")]
        [SerializeField] VisualTreeAsset _approvalCardTemplate;

        [Header("Views")]
        [Tooltip("Renders the change inside the card. Without it a card falls back to the plain preview text.")]
        [SerializeField] DiffView _diffView;

        /// <summary>The user answered. The controller hands this straight to the gate.</summary>
        public event Action<ApprovalDecision> OnApprovalAnswered;

        VisualElement _documentRootVisualElement;
        VisualElement _terminalContent;

        // The command field keeps focus while a card is open, so Escape still reaches
        // CommandInputView and cancels the turn. See RestoreFocusToTheCommandInputSoon.
        TextField _commandTextField;

        // The element key events are registered on while a card is open. Resolved once, because
        // register and unregister have to name the same element.
        VisualElement _elementThatSeesEveryKeyEvent;

        // The card currently waiting for an answer, and the two pieces of it that change when it is
        // answered. All null whenever no card is open.
        VisualElement _openCardElement;
        VisualElement _openCardKeysRow;
        Label _openCardOutcomeLabel;

        bool _isRememberKeyOfferedOnTheOpenCard;

        const string k_terminalContentElementName = "terminal-content";
        const string k_commandFieldClassName = "input-row__field";

        const string k_cardRootElementName = "approval-card";
        const string k_cardTitleElementName = "approval-card__title";
        const string k_cardWarningElementName = "approval-card__warning";
        const string k_cardTargetElementName = "approval-card__target";
        const string k_cardBodyElementName = "approval-card__body";
        const string k_cardPreviewElementName = "approval-card__preview";
        const string k_cardKeysElementName = "approval-card__keys";
        const string k_cardApproveButtonName = "approval-card__approve";
        const string k_cardRejectButtonName = "approval-card__reject";
        const string k_cardRememberButtonName = "approval-card__remember";
        const string k_cardOutcomeElementName = "approval-card__outcome";

        const string k_commandCardClassName = "approval-card--command";
        const string k_resolvedCardClassName = "approval-card--resolved";
        const string k_hiddenClassName = "approval-card__hidden";

        const string k_editCardTitleText = "FILE CHANGE — NEEDS YOUR APPROVAL";
        const string k_commandCardTitleText = "SHELL COMMAND — NEEDS YOUR APPROVAL";
        const string k_commandCardWarningText = "this runs on your machine, outside the sandbox. every command is asked about, in every mode.";

        const string k_wholeBlockReplaceNoticeText =
            "! this change was too large to pair up line by line, so it is shown as one whole block " +
            "removed and one whole block added. The change is real; the pairing inside it is not.";

        const string k_approveKeyText = "[Y] approve";
        const string k_rejectKeyText = "[N] reject";

        const string k_approvedOutcomeText = "approved";
        const string k_rejectedOutcomeText = "rejected";
        const string k_cancelledOutcomeText = "cancelled";
        const string k_supersededOutcomeText = "replaced by a newer request";

        void OnEnable()
        {
            _documentRootVisualElement = GetComponent<UIDocument>().rootVisualElement;
            _terminalContent = _documentRootVisualElement.Q<VisualElement>(k_terminalContentElementName);
            _commandTextField = _documentRootVisualElement.Q<TextField>(className: k_commandFieldClassName);
        }

        void OnDisable()
        {
            // Leaving the shortcuts registered would swallow y, n and a for the rest of the session,
            // long after the card they belonged to is gone. The gate's own cancellation is what
            // releases the loop; this only tidies up the screen.
            RetireOpenCard(k_cancelledOutcomeText);
            UnregisterKeyboardShortcuts();
        }

        /// <summary>
        /// Shows a card with only the payload the model asked for. Used when there is no diff to
        /// show - a command, or a write whose before-and-after could not be worked out.
        /// </summary>
        public void ShowRequest(ApprovalRequest approvalRequest)
        {
            ShowRequest(approvalRequest, null);
        }

        /// <summary>
        /// Shows a card with the real change rendered inside it. <paramref name="fileDiffToShow"/>
        /// is the very diff the file layer built from the text that will be written, passed whole
        /// rather than as a list of rows, because the card also has to say when the differ gave up
        /// on pairing lines - see <see cref="FileDiff.WasRenderedAsWholeBlockReplace"/>.
        /// </summary>
        public void ShowRequest(ApprovalRequest approvalRequest, FileDiff fileDiffToShow)
        {
            if (approvalRequest == null)
            {
                Debug.LogWarning("[ApprovalCardView] An empty approval request arrived, so no card was drawn.");
                return;
            }

            if (_terminalContent == null)
            {
                Debug.LogWarning("[ApprovalCardView] There is no terminal content element, so no card could be drawn.");
                return;
            }

            // The gate only ever has one request pending, so an older card still being open means
            // that request is already gone. Retire it rather than leaving two live sets of keys.
            RetireOpenCard(k_supersededOutcomeText);

            var cardContainer = CreateCardElement(approvalRequest, fileDiffToShow);
            _terminalContent.Add(cardContainer);

            RegisterKeyboardShortcuts();
        }

        /// <summary>
        /// The gate resolved the request without an answer from this card - the turn was cancelled.
        /// Safe to call at any time; it does nothing when no card is open.
        /// </summary>
        public void CloseCard()
        {
            RetireOpenCard(k_cancelledOutcomeText);
        }

        VisualElement CreateCardElement(ApprovalRequest approvalRequest, FileDiff fileDiffToShow)
        {
            var cardContainer = _approvalCardTemplate.Instantiate();

            _openCardElement = cardContainer.Q<VisualElement>(k_cardRootElementName);
            _openCardKeysRow = cardContainer.Q<VisualElement>(k_cardKeysElementName);
            _openCardOutcomeLabel = cardContainer.Q<Label>(k_cardOutcomeElementName);
            _isRememberKeyOfferedOnTheOpenCard = !approvalRequest.IsCommand;

            FillHeaderOfCard(cardContainer, approvalRequest, fileDiffToShow);
            FillBodyOfCard(cardContainer, approvalRequest, fileDiffToShow);
            WireKeyChipsOfCard(cardContainer, approvalRequest);

            return cardContainer;
        }

        void FillHeaderOfCard(VisualElement cardContainer, ApprovalRequest approvalRequest, FileDiff fileDiffToShow)
        {
            var titleLabel = cardContainer.Q<Label>(k_cardTitleElementName);
            var warningLabel = cardContainer.Q<Label>(k_cardWarningElementName);
            var targetLabel = cardContainer.Q<Label>(k_cardTargetElementName);

            titleLabel.text = approvalRequest.IsCommand ? k_commandCardTitleText : k_editCardTitleText;
            targetLabel.text = BuildTargetTextOfCard(approvalRequest, fileDiffToShow);

            _openCardElement.EnableInClassList(k_commandCardClassName, approvalRequest.IsCommand);

            warningLabel.text = k_commandCardWarningText;
            warningLabel.EnableInClassList(k_hiddenClassName, !approvalRequest.IsCommand);
        }

        // The change was too large to pair line by line, so the rows below are one block removed
        // followed by one block added. The change itself is real; only the pairing inside it is
        // not, and a reader who assumes otherwise will read the card as a much smaller edit than
        // it is. So the card says so, next to the file it applies to.
        static string BuildTargetTextOfCard(ApprovalRequest approvalRequest, FileDiff fileDiffToShow)
        {
            string targetText = $"{approvalRequest.ToolName}   {approvalRequest.TargetDescription}";

            if (fileDiffToShow == null || !fileDiffToShow.WasRenderedAsWholeBlockReplace)
            {
                return targetText;
            }

            return targetText + "\n" + k_wholeBlockReplaceNoticeText;
        }

        // The diff is the whole point of the card whenever there is one: the preview text is only
        // the payload the model asked for, while the diff is what the file will actually become.
        void FillBodyOfCard(VisualElement cardContainer, ApprovalRequest approvalRequest, FileDiff fileDiffToShow)
        {
            var bodyContainer = cardContainer.Q<VisualElement>(k_cardBodyElementName);
            var previewLabel = cardContainer.Q<Label>(k_cardPreviewElementName);

            bool canShowDiff = _diffView != null && fileDiffToShow != null && fileDiffToShow.Lines.Count > 0;

            if (!canShowDiff)
            {
                previewLabel.text = approvalRequest.PreviewText;
                return;
            }

            previewLabel.AddToClassList(k_hiddenClassName);
            bodyContainer.Add(_diffView.BuildDiffElement(approvalRequest.TargetDescription, fileDiffToShow.Lines));
        }

        void WireKeyChipsOfCard(VisualElement cardContainer, ApprovalRequest approvalRequest)
        {
            var approveButton = cardContainer.Q<Button>(k_cardApproveButtonName);
            var rejectButton = cardContainer.Q<Button>(k_cardRejectButtonName);
            var rememberButton = cardContainer.Q<Button>(k_cardRememberButtonName);

            approveButton.text = k_approveKeyText;
            rejectButton.text = k_rejectKeyText;
            rememberButton.text = $"[A] approve every {approvalRequest.ToolName} this session";

            approveButton.clicked += OnClickedApprove;
            rejectButton.clicked += OnClickedReject;
            rememberButton.clicked += OnClickedApproveForTheSession;

            // Clicking a focusable element moves focus to it, and focus leaving the command field is
            // what would break Escape while the card is open. A chip without focus still handles its
            // click, because a click comes from pointer events and not from the focus ring.
            approveButton.focusable = false;
            rejectButton.focusable = false;
            rememberButton.focusable = false;

            // A command is never remembered, in any mode, so the key that would remember it is not
            // shown at all. Offering a key that silently does nothing is worse than not offering it.
            rememberButton.EnableInClassList(k_hiddenClassName, !_isRememberKeyOfferedOnTheOpenCard);
        }

        // Registered on the PANEL root, which sits above the document root, so the shortcut sees the
        // key before it reaches anything else - including CommandInputView, whose own handlers are
        // registered on the input field with TrickleDown and would otherwise get there first.
        // Registered only while a card is open, so ordinary typing is never intercepted.
        void RegisterKeyboardShortcuts()
        {
            // Registering twice would answer one press twice, so any earlier registration goes
            // first. It is normally already gone - this is the belt to the braces above.
            UnregisterKeyboardShortcuts();

            _elementThatSeesEveryKeyEvent = ResolveElementThatSeesEveryKeyEvent();
            _elementThatSeesEveryKeyEvent.RegisterCallback<KeyDownEvent>(OnKeyDownWhileCardIsOpen, TrickleDown.TrickleDown);
        }

        VisualElement ResolveElementThatSeesEveryKeyEvent()
        {
            return _documentRootVisualElement.panel != null
                ? _documentRootVisualElement.panel.visualTree
                : _documentRootVisualElement;
        }

        void UnregisterKeyboardShortcuts()
        {
            if (_elementThatSeesEveryKeyEvent == null) return;

            _elementThatSeesEveryKeyEvent.UnregisterCallback<KeyDownEvent>(OnKeyDownWhileCardIsOpen, TrickleDown.TrickleDown);
            _elementThatSeesEveryKeyEvent = null;
        }

        void OnKeyDownWhileCardIsOpen(KeyDownEvent keyDownEvent)
        {
            if (_openCardElement == null) return;

            // Escape is deliberately NOT handled here. It belongs to CommandInputView, which cancels
            // the running turn; the gate then completes its handshake and closes this card. Handling
            // it here would give the user two different ways to stop, one of which leaves the loop
            // running with nothing on screen.
            char pressedCharacter = ResolvePressedCharacter(keyDownEvent);

            if (pressedCharacter == 'y')
            {
                AnswerOpenCard(true, false);
            }
            else if (pressedCharacter == 'n')
            {
                AnswerOpenCard(false, false);
            }
            else if (pressedCharacter == 'a' && _isRememberKeyOfferedOnTheOpenCard)
            {
                AnswerOpenCard(true, true);
            }
            else
            {
                return;
            }

            // Stopping here in the trickle-down phase means the key never reaches the input field at
            // all, so CommandInputView's own handlers never run and nothing is typed into the row.
            keyDownEvent.StopPropagation();
        }

        // UI Toolkit sends one key press as two events - one carrying the keyCode with no character,
        // one carrying the character with keyCode None - and which arrives first depends on the
        // platform. Reading both means the press is recognised either way, and the card is already
        // retired by the time the second half arrives, so it cannot answer twice.
        static char ResolvePressedCharacter(KeyDownEvent keyDownEvent)
        {
            if (keyDownEvent.keyCode == KeyCode.Y) return 'y';
            if (keyDownEvent.keyCode == KeyCode.N) return 'n';
            if (keyDownEvent.keyCode == KeyCode.A) return 'a';

            return char.ToLowerInvariant(keyDownEvent.character);
        }

        void OnClickedApprove()
        {
            AnswerOpenCard(true, false);
        }

        void OnClickedReject()
        {
            AnswerOpenCard(false, false);
        }

        void OnClickedApproveForTheSession()
        {
            AnswerOpenCard(true, true);
        }

        void AnswerOpenCard(bool isApproved, bool shouldRememberForSession)
        {
            if (_openCardElement == null) return;

            var approvalDecision = new ApprovalDecision(isApproved, shouldRememberForSession);

            RetireOpenCard(BuildOutcomeText(isApproved, shouldRememberForSession));
            OnApprovalAnswered?.Invoke(approvalDecision);
        }

        static string BuildOutcomeText(bool isApproved, bool shouldRememberForSession)
        {
            if (!isApproved) return k_rejectedOutcomeText;

            return shouldRememberForSession
                ? k_approvedOutcomeText + " — and every call to this tool for the rest of the session"
                : k_approvedOutcomeText;
        }

        // The card is never removed. It stays in the log dimmed, with the keys replaced by what was
        // decided, because the transcript of a session is the record of what the agent was allowed
        // to do - and a card that vanishes takes that record with it.
        void RetireOpenCard(string outcomeText)
        {
            if (_openCardElement == null) return;

            _openCardElement.AddToClassList(k_resolvedCardClassName);
            _openCardKeysRow.AddToClassList(k_hiddenClassName);

            _openCardOutcomeLabel.text = outcomeText;
            _openCardOutcomeLabel.RemoveFromClassList(k_hiddenClassName);

            _openCardElement = null;
            _openCardKeysRow = null;
            _openCardOutcomeLabel = null;
            _isRememberKeyOfferedOnTheOpenCard = false;

            UnregisterKeyboardShortcuts();
            RestoreFocusToTheCommandInputSoon();
        }

        // Clicking a chip can leave the panel with nothing focused, and key events then stop
        // reaching the input field - which is where Escape-to-cancel lives. Putting focus back
        // keeps the terminal's one keyboard path intact whether the card was answered by key or by
        // click. Scheduled rather than called directly, because focus set during event dispatch is
        // overwritten by the focus controller that is still finishing the same event.
        void RestoreFocusToTheCommandInputSoon()
        {
            if (_commandTextField == null) return;

            _commandTextField.schedule.Execute(() => _commandTextField.Focus());
        }
    }
}
