using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Shows a temporary "thinking" line between the user's command and the answer. It types the
    /// word out, then cycles ASCII spinner frames for exactly as long as the work it covers takes,
    /// and removes itself afterwards - even when that work throws.
    /// </summary>
    public class SpinnerView : MonoBehaviour
    {
        VisualElement _terminalContent;

        // One word, not a rotation. This spinner covers only the silence before the first token,
        // and ThinkingBlock takes over from it on that token with a spinner of its own. When the
        // two said different words - "planning" here, "thinking" there - the hand-over read as one
        // indicator being replaced by another rather than as work carrying on.
        const string k_wordShownWhileWorking = "thinking";

        const float k_phraseCharacterDelaySeconds = 0.02f;
        const string k_spinnerLineClassName = "line--tool-text";

        void OnEnable()
        {
            _terminalContent = GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>("terminal-content");
        }

        /// <summary>
        /// Covers an already-running <paramref name="work"/> task, so the spinner overlaps the work
        /// and lasts exactly as long as it does, then returns the work's result.
        /// </summary>
        public async UniTask<TResult> ShowUntilAsync<TResult>(UniTask<TResult> work, CancellationToken cancellationToken)
        {
            var spinnerLabel = new Label();
            spinnerLabel.AddToClassList(k_spinnerLineClassName);
            _terminalContent.Add(spinnerLabel);

            try
            {
                await TerminalTextAnimator.TypeTextWithBlinkingCursorAsync(spinnerLabel, k_wordShownWhileWorking, k_phraseCharacterDelaySeconds, cancellationToken);
                return await RunSpinnerFramesUntilAsync(spinnerLabel, work);
            }
            finally
            {
                spinnerLabel.RemoveFromHierarchy();
            }
        }

        // Once the word is typed, LineSpinner swaps the trailing frame on a fixed beat until the
        // work completes. The frames and the beat live there, so this spinner and the one on the
        // thought line stay in step.
        async UniTask<TResult> RunSpinnerFramesUntilAsync<TResult>(Label spinnerLabel, UniTask<TResult> work)
        {
            var lineSpinner = new LineSpinner();
            lineSpinner.Start(spinnerLabel, k_wordShownWhileWorking);

            try
            {
                return await work;
            }
            finally
            {
                lineSpinner.Stop(spinnerLabel.text);
            }
        }
    }
}
