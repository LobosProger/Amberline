using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Shows a temporary "working" line between the user's command and the answer. It types out
    /// one of the rotating phrases, then cycles ASCII spinner frames for exactly as long as the
    /// work it covers takes, and removes itself afterwards - even when that work throws.
    /// </summary>
    public class SpinnerView : MonoBehaviour
    {
        VisualElement _terminalContent;

        // Rotates through the phrases so a long session does not repeat the same word every turn.
        // An instance field rather than a static one, so re-entering Play mode starts fresh.
        int _currentPhraseIndex;

        static readonly string[] k_spinnerFrames = { "[ \\ ]", "[ | ]", "[ / ]", "[ | ]" };
        static readonly string[] k_workingPhrases =
        {
            "thinking",
            "reading",
            "reasoning",
            "planning",
            "working"
        };

        const long k_spinnerFrameIntervalMs = 120;
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

            var phrase = PickNextPhrase();

            try
            {
                await TerminalTextAnimator.TypeTextWithBlinkingCursorAsync(spinnerLabel, phrase, k_phraseCharacterDelaySeconds, cancellationToken);
                return await RunSpinnerFramesUntilAsync(spinnerLabel, phrase, work, cancellationToken);
            }
            finally
            {
                spinnerLabel.RemoveFromHierarchy();
            }
        }

        string PickNextPhrase()
        {
            var phrase = k_workingPhrases[_currentPhraseIndex];
            _currentPhraseIndex = (_currentPhraseIndex + 1) % k_workingPhrases.Length;
            return phrase;
        }

        // Once the phrase is typed, keep swapping the trailing frame on a fixed beat until the
        // work completes, then stop the schedule and hand the work's result back.
        async UniTask<TResult> RunSpinnerFramesUntilAsync<TResult>(Label spinnerLabel, string phrase, UniTask<TResult> work, CancellationToken cancellationToken)
        {
            int currentFrameIndex = 0;
            spinnerLabel.text = phrase + " " + k_spinnerFrames[0];

            var spinnerFrameSchedule = spinnerLabel.schedule.Execute(() =>
            {
                currentFrameIndex = (currentFrameIndex + 1) % k_spinnerFrames.Length;
                spinnerLabel.text = phrase + " " + k_spinnerFrames[currentFrameIndex];
            }).Every(k_spinnerFrameIntervalMs);

            try
            {
                return await work;
            }
            finally
            {
                spinnerFrameSchedule.Pause();
            }
        }
    }
}
