using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    // The handshake between the agent loop and the person watching it. ToolRunner awaits
    // RequestApprovalAsync before any mutating or command call runs. This class draws nothing: it
    // raises OnApprovalRequested, the terminal renders a card, the user answers, and SubmitDecision
    // completes the task the loop is sitting on.
    //
    // No type from Amberline.Ui appears here, and none ever may. The gate is a plain class rather
    // than a MonoBehaviour because it holds no scene state - AgentRunner owns one for the session
    // and hands RequestApprovalAsync to ToolRunner as the injected delegate.
    //
    // Five responsibilities, each of them present because the alternative is a bug:
    //
    // 1. IT MARSHALS TO THE MAIN THREAD. The loop runs the model and its file IO off the main
    //    thread, so RequestApprovalAsync is entered from a thread pool thread. UI Toolkit may only
    //    be touched from the main thread, so the switch happens here rather than in every
    //    subscriber - a view that has to remember to marshal will eventually forget.
    //
    // 2. RUN_COMMAND IS ASKED ABOUT IN EVERY MODE. A shell command never auto-approves, never
    //    enters the session allow-list, and is not affected by AutoApproveEdits. That is a fixed
    //    product decision, not a default someone may flip later: a command is the only thing the
    //    agent can ask for that reaches outside the workspace entirely.
    //
    // 3. IT RE-READS THE TARGET AFTER THE ANSWER. The card shows a change worked out from the file
    //    as it was when the card was drawn. If the file changes while the card sits on screen - the
    //    user saves it in their editor, a build regenerates it - approving would write a payload
    //    computed against a file that no longer exists in that form. So the target is fingerprinted
    //    before the card is drawn and again the moment the answer arrives, and a difference re-asks
    //    with the change spelled out instead of writing.
    //
    // 4. CANCELLATION COMPLETES THE HANDSHAKE. Escape while a card is open cancels the turn. The
    //    completion source is completed from the token registration, so the loop unblocks and ends
    //    the run instead of waiting forever on a card nobody will ever answer.
    //
    // 5. ONE CARD AT A TIME. A second request arriving while one is pending is refused rather than
    //    queued. The loop only ever has one tool call in flight, so a second request means
    //    something is wrong, and queueing would hide it behind a stack of cards.
    //
    // 6. A REJECTED CALL IS REMEMBERED AND NEVER ASKED ABOUT TWICE. Measured in M4: told plainly
    //    that the user had rejected an edit, Qwen3-8B re-sent the same one three times in a row.
    //    RepeatedCallDetector caught none of them - one retry had a read_file wedged in between and
    //    another varied the find anchor just enough to look like a different call. So the gate
    //    matches on TOOL NAME PLUS TARGET rather than on the arguments, which is the part the model
    //    cannot vary without asking for something genuinely different, and the second such call is
    //    refused with no card at all. A rejection then costs the user one keypress, not one per
    //    iteration until the loop reaches its cap.
    public class ApprovalGate
    {
        /// <summary>
        /// A card must be drawn. Always raised on the main thread, and always followed by exactly
        /// one <see cref="OnApprovalResolved"/>.
        /// </summary>
        public event Action<ApprovalRequest> OnApprovalRequested;

        /// <summary>
        /// The pending request stopped being pending, for any reason - answered, cancelled, or the
        /// same call being re-asked because its target changed. The view retires its card on this.
        /// </summary>
        public event Action OnApprovalResolved;

        // Only ever used to turn the request's target into an absolute path for fingerprinting.
        // Nothing here writes, and nothing here decides what a tool may touch - PathSandbox stays
        // the one place that turns model text into a path a tool acts on.
        PathSandbox _pathSandbox;

        PermissionMode _permissionMode = PermissionMode.AskEveryTime;

        // Tool names the user answered "approve every one of these this session" about. A command
        // can never land in here, which is enforced twice: the card never offers the key, and
        // RememberToolForTheSessionWhenTheUserAskedFor refuses it again.
        readonly HashSet<string> _toolNamesApprovedForTheWholeSession = new HashSet<string>(StringComparer.Ordinal);

        // Tool-name-plus-target keys the user has said no to. Unlike the allow-list above, a
        // command CAN land in here: refusing to ask a second time is not the same as approving
        // without asking, and only one of those two is forbidden for a command.
        readonly HashSet<string> _keysOfCallsTheUserRejected = new HashSet<string>(StringComparer.Ordinal);

        // The one card that may be open. Null whenever nothing is waiting on the user.
        UniTaskCompletionSource<ApprovalDecision> _pendingApprovalAnswer;

        // Shared because both are immutable and the gate hands them back on every automatic path.
        static readonly ApprovalDecision k_approvedWithoutRemembering = new ApprovalDecision(true, false);
        static readonly ApprovalDecision k_rejectedDecision = new ApprovalDecision(false, false);
        static readonly ApprovalDecision k_rejectedBecauseItWasRejectedEarlier = new ApprovalDecision(false, false, true);

        // How many times one call may be re-asked because its target moved under it. Two covers a
        // save landing mid-card; a file changing on every attempt is a build writing to it, and
        // asking a fourth time would only be a loop the user cannot win.
        const int k_maximumNumberOfTimesOneCallIsReAsked = 2;

        // Files bigger than this are fingerprinted by size and write time instead of by content,
        // because the fingerprint is taken on the main thread and reading megabytes twice per
        // approval would drop frames for a question the write time already answers.
        const long k_maximumFileBytesToFingerprintByContent = 1024L * 1024L;

        const string k_fingerprintOfAMissingFile = "absent";
        const string k_fingerprintThatIsNotChecked = "unchecked";

        // No tool name and no path can contain it, so two different calls can never flatten into
        // one key. Written as a code because it has no printable form.
        const char k_separatorBetweenToolAndTarget = (char)1;

        // The two magic numbers of FNV-1a, 64 bit.
        const ulong k_fnvOffsetBasis = 14695981039346656037UL;
        const ulong k_fnvPrime = 1099511628211UL;

        const string k_targetChangedNoticeText =
            "! this file changed on disk while the card was open, so the change below was worked out " +
            "against an older version of it. Look again before approving.";

        /// <summary>How much the user is being asked right now. Backs /approve-mode.</summary>
        public PermissionMode CurrentPermissionMode => _permissionMode;

        /// <summary>True while a card is on screen and the loop is waiting for an answer.</summary>
        public bool IsWaitingForTheUser => _pendingApprovalAnswer != null;

        /// <summary>
        /// Points the gate at the sandbox that resolves paths for the current workspace. Changing
        /// workspace also forgets every session approval, because "yes to every write_file" was
        /// granted over one project and must not carry into the next one.
        /// </summary>
        public void SetPathSandbox(PathSandbox pathSandbox)
        {
            _pathSandbox = pathSandbox;
            _toolNamesApprovedForTheWholeSession.Clear();
            _keysOfCallsTheUserRejected.Clear();
        }

        /// <summary>
        /// Switches how much the user is asked. Commands are unaffected in every mode. Tightening
        /// the mode also drops the session allow-list, so "ask me every time" really does.
        /// </summary>
        public void SetPermissionMode(PermissionMode permissionMode)
        {
            _permissionMode = permissionMode;

            if (permissionMode == PermissionMode.AskEveryTime)
            {
                _toolNamesApprovedForTheWholeSession.Clear();
            }
        }

        /// <summary>Drops every session approval. Backs /clear, which starts the session over.</summary>
        public void ForgetApprovalsRememberedForTheSession()
        {
            _toolNamesApprovedForTheWholeSession.Clear();
        }

        /// <summary>
        /// Forgets which calls the user rejected. Called at the start of every run, so a rejection
        /// binds the run it was given in and no longer - a user who asks for the same thing on the
        /// next turn is asked about it again - and by /clear, which starts everything over.
        /// </summary>
        public void ForgetCallsTheUserRejected()
        {
            _keysOfCallsTheUserRejected.Clear();
        }

        /// <summary>
        /// The delegate ToolRunner is constructed with. Returns the user's answer, or an automatic
        /// one when the mode or the session allow-list already covers this tool. Throws
        /// <see cref="OperationCanceledException"/> when the run is cancelled while the card is
        /// open - ToolRunner rethrows it, so the model never learns the user pressed Escape.
        /// </summary>
        public async UniTask<ApprovalDecision> RequestApprovalAsync(ApprovalRequest approvalRequest, CancellationToken cancellationToken)
        {
            if (approvalRequest == null)
            {
                Debug.LogWarning("[ApprovalGate] An empty approval request arrived, so it was rejected.");
                return k_rejectedDecision;
            }

            // Everything below this line runs on the main thread: it raises events straight into
            // UI Toolkit, which cannot be touched from anywhere else.
            await UniTask.SwitchToMainThread(cancellationToken);

            var automaticDecision = TryDecideWithoutAskingTheUser(approvalRequest);
            if (automaticDecision != null)
            {
                return automaticDecision;
            }

            if (IsWaitingForTheUser)
            {
                Debug.LogWarning($"[ApprovalGate] {approvalRequest.ToolName} asked for approval while another card was still open, so it was rejected.");
                return k_rejectedDecision;
            }

            return await AskUntilTheTargetStopsChangingAsync(approvalRequest, cancellationToken);
        }

        // Returns the answer when no card is needed, and null when the user has to be asked.
        ApprovalDecision TryDecideWithoutAskingTheUser(ApprovalRequest approvalRequest)
        {
            // First, and above the command rule below it: a no the user has already given is
            // honoured for a command too. "Commands are always asked about" means the user can
            // never be bypassed into one, not that they must keep answering the same question.
            if (_keysOfCallsTheUserRejected.Contains(BuildKeyOfCall(approvalRequest)))
            {
                return k_rejectedBecauseItWasRejectedEarlier;
            }

            // Fixed decision: a shell command passes the gate in every mode. Neither the permission
            // mode nor the session allow-list is even consulted for one.
            if (approvalRequest.IsCommand)
            {
                return null;
            }

            if (_permissionMode == PermissionMode.AutoApproveEdits)
            {
                return k_approvedWithoutRemembering;
            }

            if (_toolNamesApprovedForTheWholeSession.Contains(approvalRequest.ToolName))
            {
                return k_approvedWithoutRemembering;
            }

            return null;
        }

        // Asks, then checks that the file the preview was drawn against is still the file on disk.
        // If it is not, the same call is asked again with the change called out, so an approval is
        // always an approval of what is about to land.
        async UniTask<ApprovalDecision> AskUntilTheTargetStopsChangingAsync(ApprovalRequest approvalRequest, CancellationToken cancellationToken)
        {
            var requestToShow = approvalRequest;

            for (int askAttemptIndex = 0; askAttemptIndex <= k_maximumNumberOfTimesOneCallIsReAsked; askAttemptIndex++)
            {
                string fingerprintBeforeTheCardWasDrawn = TakeFingerprintOfApprovalTarget(approvalRequest);

                var decisionFromTheUser = await AskTheUserAsync(requestToShow, cancellationToken);

                if (!decisionFromTheUser.IsApproved)
                {
                    RememberThatTheUserRejectedThisCall(approvalRequest);
                    return decisionFromTheUser;
                }

                // Read the target again NOW. The write runs the moment this method returns, so this
                // is the last point at which the gate can tell approved-and-unchanged apart from
                // approved-and-something-else-happened-meanwhile.
                string fingerprintAfterTheAnswer = TakeFingerprintOfApprovalTarget(approvalRequest);

                if (string.Equals(fingerprintBeforeTheCardWasDrawn, fingerprintAfterTheAnswer, StringComparison.Ordinal))
                {
                    RememberToolForTheSessionWhenTheUserAskedFor(approvalRequest, decisionFromTheUser);
                    return decisionFromTheUser;
                }

                requestToShow = BuildRequestThatSaysTheTargetChanged(approvalRequest);
            }

            Debug.LogWarning($"[ApprovalGate] {approvalRequest.ToolName} kept finding its target changed, so it was refused.");
            return k_rejectedDecision;
        }

        // Identifies the exact bytes on disk. Content beats size and write time whenever the file is
        // small enough to read cheaply, because an edit that keeps the length and lands inside the
        // same write-time tick is invisible to the cheaper test.
        //
        // Two answers are deliberately not fingerprints of anything: a command has no target file at
        // all, and a target the sandbox refuses is one no tool could write either, so both come back
        // as a constant that compares equal to itself and never triggers a re-ask.
        string TakeFingerprintOfApprovalTarget(ApprovalRequest approvalRequest)
        {
            if (approvalRequest.IsCommand)
            {
                return k_fingerprintThatIsNotChecked;
            }

            if (_pathSandbox == null || string.IsNullOrWhiteSpace(approvalRequest.TargetDescription))
            {
                return k_fingerprintThatIsNotChecked;
            }

            if (!_pathSandbox.TryResolvePath(approvalRequest.TargetDescription, out string absoluteTargetPath, out _))
            {
                return k_fingerprintThatIsNotChecked;
            }

            return BuildFingerprintOfFile(absoluteTargetPath);
        }

        static string BuildFingerprintOfFile(string absoluteFilePath)
        {
            try
            {
                var targetFileInfo = new FileInfo(absoluteFilePath);

                // A file that does not exist yet still has a fingerprint: "absent". A create that
                // turns into an overwrite between the card and the answer is exactly the kind of
                // drift this check exists for.
                if (!targetFileInfo.Exists)
                {
                    return k_fingerprintOfAMissingFile;
                }

                if (targetFileInfo.Length > k_maximumFileBytesToFingerprintByContent)
                {
                    return $"{targetFileInfo.Length}:{targetFileInfo.LastWriteTimeUtc.Ticks}";
                }

                return BuildContentHashOfFile(absoluteFilePath, targetFileInfo.Length);
            }
            catch (Exception exception)
            {
                // A target we cannot even stat is one the write will fail on anyway. Failing to
                // fingerprint it must not take the approval down with it.
                Debug.LogWarning($"[ApprovalGate] The target could not be fingerprinted: {exception.GetType().Name}: {exception.Message}");
                return k_fingerprintThatIsNotChecked;
            }
        }

        // FNV-1a over the raw bytes. Not a security hash and it does not need to be - it answers one
        // question, "are these the same bytes I drew a change against", and it answers it without
        // pulling a crypto provider into a path that runs on the main thread.
        static string BuildContentHashOfFile(string absoluteFilePath, long fileLengthInBytes)
        {
            byte[] fileBytes = File.ReadAllBytes(absoluteFilePath);
            ulong hashValue = k_fnvOffsetBasis;

            foreach (byte fileByte in fileBytes)
            {
                hashValue = (hashValue ^ fileByte) * k_fnvPrime;
            }

            return $"{fileLengthInBytes}:{hashValue:x16}";
        }

        // Raises the request, then parks on the completion source until the view answers or the
        // token fires. Everything that can end the wait completes the same object, so there is no
        // path out of here that leaves the loop awaiting.
        async UniTask<ApprovalDecision> AskTheUserAsync(ApprovalRequest approvalRequest, CancellationToken cancellationToken)
        {
            _pendingApprovalAnswer = new UniTaskCompletionSource<ApprovalDecision>();

            // Registering rather than racing: if the token is already cancelled the callback runs
            // inside Register, the source is completed before the await, and the await throws
            // straight away instead of parking on a card that will never be drawn.
            using (cancellationToken.Register(CancelPendingApproval))
            {
                try
                {
                    OnApprovalRequested?.Invoke(approvalRequest);
                    return await _pendingApprovalAnswer.Task;
                }
                finally
                {
                    _pendingApprovalAnswer = null;
                    OnApprovalResolved?.Invoke();
                }
            }
        }

        // Cancellation is requested from the terminal's Escape handler, which runs on the main
        // thread, and UniTask resumes the awaiting method inline on the completing thread - so the
        // finally above, and the OnApprovalResolved it raises, stay on the main thread too.
        void CancelPendingApproval()
        {
            _pendingApprovalAnswer?.TrySetCanceled();
        }

        /// <summary>
        /// The view's half of the handshake: the user pressed a key or clicked a chip. A decision
        /// that arrives with no card open is ignored with a warning, and a null decision counts as a
        /// rejection - silence must never mean yes for a call that writes to disk.
        /// </summary>
        public void SubmitDecision(ApprovalDecision approvalDecision)
        {
            var pendingApprovalAnswer = _pendingApprovalAnswer;

            if (pendingApprovalAnswer == null)
            {
                Debug.LogWarning("[ApprovalGate] A decision arrived with no card open, so it was ignored.");
                return;
            }

            pendingApprovalAnswer.TrySetResult(approvalDecision ?? k_rejectedDecision);
        }

        void RememberToolForTheSessionWhenTheUserAskedFor(ApprovalRequest approvalRequest, ApprovalDecision approvalDecision)
        {
            if (!approvalDecision.ShouldRememberForSession)
            {
                return;
            }

            // The card never offers the key for a command. This is the second lock on the same
            // door, so a future front end cannot open it by mistake.
            if (approvalRequest.IsCommand)
            {
                Debug.LogWarning("[ApprovalGate] A command asked to be remembered for the session. Commands are always asked about, so it was not.");
                return;
            }

            _toolNamesApprovedForTheWholeSession.Add(approvalRequest.ToolName);
        }

        void RememberThatTheUserRejectedThisCall(ApprovalRequest approvalRequest)
        {
            _keysOfCallsTheUserRejected.Add(BuildKeyOfCall(approvalRequest));
        }

        // Tool name plus target, and deliberately NOT the arguments. A model that has just been
        // told no re-sends the same intent with the payload nudged - a different find anchor, one
        // more line of context - and an argument hash lets every one of those through. What it
        // cannot change without asking for something genuinely different is which file, or which
        // command.
        static string BuildKeyOfCall(ApprovalRequest approvalRequest)
        {
            return approvalRequest.ToolName + k_separatorBetweenToolAndTarget +
                   NormaliseTargetForMatching(approvalRequest.TargetDescription);
        }

        // The same file reached as src\A.cs and as src/A.cs, and the same command written with two
        // spaces instead of one, must land on the same key - those are exactly the differences a
        // retry produces without meaning anything by them.
        static string NormaliseTargetForMatching(string targetDescription)
        {
            if (string.IsNullOrEmpty(targetDescription))
            {
                return string.Empty;
            }

            var normalisedTarget = new StringBuilder(targetDescription.Length);
            bool isPreviousCharacterWhitespace = false;

            foreach (char targetCharacter in targetDescription.Trim())
            {
                if (char.IsWhiteSpace(targetCharacter))
                {
                    if (!isPreviousCharacterWhitespace)
                    {
                        normalisedTarget.Append(' ');
                    }

                    isPreviousCharacterWhitespace = true;
                    continue;
                }

                isPreviousCharacterWhitespace = false;
                normalisedTarget.Append(char.ToLowerInvariant(targetCharacter == '\\' ? '/' : targetCharacter));
            }

            return normalisedTarget.ToString();
        }

        // The re-ask carries the same tool, target and payload - only the preview gains a line
        // saying why it is being asked twice. Built rather than mutated, because ApprovalRequest is
        // immutable and every reader of it should stay able to rely on that.
        static ApprovalRequest BuildRequestThatSaysTheTargetChanged(ApprovalRequest approvalRequest)
        {
            return new ApprovalRequest(
                approvalRequest.ToolName,
                approvalRequest.TargetDescription,
                k_targetChangedNoticeText + "\n" + approvalRequest.PreviewText,
                approvalRequest.IsCommand,
                approvalRequest.Call);
        }
    }
}
