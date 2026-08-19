using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Shared "typewriter with blinking cursor" animation for UI Toolkit <see cref="Label"/> elements.
    /// Used by <see cref="TerminalView"/> for the boot sequence and by <see cref="SpinnerView"/> for the
    /// loading phrase, so the effect stays consistent across the terminal.
    /// </summary>
    public static class TerminalTextAnimator
    {
        const string k_cursorCharacter = "█";
        const long k_cursorBlinkIntervalMs = 500;

        /// <summary>
        /// Streams <paramref name="textToType"/> into <paramref name="targetLabel"/> one character at
        /// a time while a cursor blinks at the caret position. When the typing finishes (or is
        /// cancelled / skipped) the label is set to the full text without the cursor.
        /// </summary>
        /// <param name="targetLabel">Label that receives the partial text on every step.</param>
        /// <param name="textToType">Full text to animate into the label.</param>
        /// <param name="delayBetweenCharactersInSeconds">Pause between two consecutive characters.</param>
        /// <param name="cancellationToken">Cancellation token, normally the caller's destroy token.</param>
        /// <param name="isSkipRequested">
        /// Optional check polled on every character. When it returns <c>true</c> the rest of the
        /// animation is skipped and the label jumps straight to the final text. Pass <c>null</c>
        /// to disable skipping.
        /// </param>
        public static async UniTask TypeTextWithBlinkingCursorAsync(
            Label targetLabel,
            string textToType,
            float delayBetweenCharactersInSeconds,
            CancellationToken cancellationToken,
            Func<bool> isSkipRequested = null)
        {
            int currentCharacterIndex = 0;
            bool isCursorVisible = true;

            // Blink the cursor on its own schedule so it keeps pulsing while characters
            // are still appearing on screen.
            var cursorBlinkSchedule = targetLabel.schedule.Execute(() =>
            {
                isCursorVisible = !isCursorVisible;
                string alreadyTypedText = textToType.Substring(0, currentCharacterIndex);
                targetLabel.text = isCursorVisible ? alreadyTypedText + k_cursorCharacter : alreadyTypedText;
            }).Every(k_cursorBlinkIntervalMs);

            // Start with just the cursor so the caret is visible before any text appears.
            targetLabel.text = k_cursorCharacter;

            try
            {
                for (int characterIndex = 0; characterIndex < textToType.Length; characterIndex++)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (isSkipRequested != null && isSkipRequested()) break;

                    currentCharacterIndex = characterIndex + 1;
                    targetLabel.text = textToType.Substring(0, currentCharacterIndex) + (isCursorVisible ? k_cursorCharacter : "");

                    await UniTask.WaitForSeconds(delayBetweenCharactersInSeconds, cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a normal way for this animation to end - the caller is shutting
                // down or the user skipped ahead. Swallow it so the finally block still commits the
                // full text instead of leaving a half-typed line with a frozen cursor on screen.
            }
            finally
            {
                cursorBlinkSchedule.Pause();
                targetLabel.text = textToType;
            }
        }
    }
}
