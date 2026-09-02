using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Amberline.Agent
{
    /// <summary>
    /// Lets the agent stop and ask the person a question, then carry on with the answer. The same
    /// handshake <see cref="ApprovalGate"/> uses: this class raises an event, the terminal draws
    /// the question and unlocks the input row, and <see cref="SubmitAnswer"/> completes the task
    /// the loop is parked on.
    /// </summary>
    /// <remarks>
    /// It exists because the model had exactly two ways out of a run - call finish, or spend the
    /// round-trip budget - and neither is right when the task is simply underspecified. Asked to
    /// rename a function without being told the new name, a small model guesses, and the guess is
    /// what the user has to undo.
    /// <para>
    /// The run is NOT ended and restarted around the answer. Ending it would throw away the
    /// transcript the question came out of and hand the model a fresh budget, so what came back
    /// would be an answer to a question it no longer remembers asking.
    /// </para>
    /// <para>
    /// No type from Amberline.Ui appears here, and none ever may. A plain class rather than a
    /// MonoBehaviour, because it holds no scene state - AgentRunner owns one for the session.
    /// </para>
    /// </remarks>
    public class UserQuestionGate
    {
        /// <summary>
        /// A question has to be shown and the input row unlocked. Always raised on the main
        /// thread, and always followed by exactly one <see cref="OnQuestionResolved"/>.
        /// </summary>
        public event Action<string> OnQuestionAsked;

        /// <summary>
        /// The question stopped being pending, for any reason - answered or cancelled. The
        /// terminal locks the input row again on this.
        /// </summary>
        public event Action OnQuestionResolved;

        // The one question that may be open. Null whenever nothing is waiting on the user.
        UniTaskCompletionSource<string> _pendingAnswer;

        /// <summary>True while a question is on screen and the loop is waiting for an answer.</summary>
        public bool IsWaitingForTheUser => _pendingAnswer != null;

        /// <summary>
        /// Shows the question and parks the run until the user types something back. Throws
        /// <see cref="OperationCanceledException"/> when the run is cancelled while the question is
        /// open, which the tool runner rethrows - so the model never learns the user pressed Escape
        /// and never has an error about it to apologise for.
        /// </summary>
        public async UniTask<string> AskAsync(string questionText, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(questionText))
            {
                Debug.LogWarning("[UserQuestionGate] An empty question arrived, so nothing was asked.");
                return string.Empty;
            }

            // Everything below this line runs on the main thread: it raises events straight into
            // UI Toolkit, which cannot be touched from anywhere else, and the tool that called us
            // may well be running on the thread pool.
            await UniTask.SwitchToMainThread(cancellationToken);

            if (IsWaitingForTheUser)
            {
                Debug.LogWarning("[UserQuestionGate] A question arrived while another one was still open, so it was refused.");
                return string.Empty;
            }

            return await AskAndWaitForTheAnswerAsync(questionText, cancellationToken);
        }

        // Raises the question, then parks on the completion source until the terminal answers or
        // the token fires. Everything that can end the wait completes the same object, so there is
        // no path out of here that leaves the loop awaiting.
        async UniTask<string> AskAndWaitForTheAnswerAsync(string questionText, CancellationToken cancellationToken)
        {
            _pendingAnswer = new UniTaskCompletionSource<string>();

            // Registering rather than racing: if the token is already cancelled the callback runs
            // inside Register, the source is completed before the await, and the await throws
            // straight away instead of parking on a question that will never be shown.
            using (cancellationToken.Register(CancelPendingQuestion))
            {
                try
                {
                    OnQuestionAsked?.Invoke(questionText);
                    return await _pendingAnswer.Task;
                }
                finally
                {
                    _pendingAnswer = null;
                    OnQuestionResolved?.Invoke();
                }
            }
        }

        /// <summary>
        /// Cancels whatever question is open. Called from the token registration above, and by the
        /// runner when a run ends any other way, so a question can never outlive its run.
        /// </summary>
        public void CancelPendingQuestion()
        {
            _pendingAnswer?.TrySetCanceled();
        }

        /// <summary>
        /// The terminal's half of the handshake: the user typed a line while a question was open.
        /// An answer that arrives with no question open is ignored with a warning.
        /// </summary>
        public void SubmitAnswer(string answerText)
        {
            var pendingAnswer = _pendingAnswer;

            if (pendingAnswer == null)
            {
                Debug.LogWarning("[UserQuestionGate] An answer arrived with no question open, so it was ignored.");
                return;
            }

            pendingAnswer.TrySetResult(answerText ?? string.Empty);
        }
    }
}
