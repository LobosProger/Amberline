using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Cycles ASCII spinner frames at the end of one terminal line, for exactly as long as the work
    /// behind that line lasts. Unlike <see cref="SpinnerView"/>, which owns a temporary line of its
    /// own and removes it afterwards, this animates a line the caller already put in the log and
    /// leaves it there, frozen on whatever text <see cref="Stop"/> was given.
    /// <para>
    /// It is a plain class rather than a MonoBehaviour because a run needs several at once - one
    /// for the thought and one for the tool that is running - and each belongs to its own line.
    /// </para>
    /// </summary>
    public class LineSpinner
    {
        Label _labelBeingAnimated;
        string _prefixText;
        IVisualElementScheduledItem _frameSchedule;
        int _currentFrameIndex;

        // Public so SpinnerView animates on the same beat with the same frames. Two spinners that
        // look almost alike read as a glitch, and the two do appear one after the other: this one
        // takes over the moment SpinnerView's line goes away.
        public static readonly string[] k_spinnerFrames = { "[ \\ ]", "[ | ]", "[ / ]", "[ | ]" };

        public const long k_frameIntervalMilliseconds = 120;

        const string k_separatorBeforeTheFrame = "  ";

        /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>. Lets a caller skip
        /// finishing a spinner it never started, so a cancelled turn stays quiet.</summary>
        public bool IsRunning => _frameSchedule != null;

        /// <summary>
        /// Takes over <paramref name="lineLabel"/> and spins on the end of
        /// <paramref name="prefixText"/> until <see cref="Stop"/>. Starting a second time drops the
        /// first line where it stands rather than animating two at once.
        /// </summary>
        public void Start(Label lineLabel, string prefixText)
        {
            StopAnimatingWithoutTouchingTheText();

            if (lineLabel == null) return;

            _labelBeingAnimated = lineLabel;
            _prefixText = prefixText;
            _currentFrameIndex = 0;

            _labelBeingAnimated.text = BuildTextForCurrentFrame();

            // Scheduled on the element itself, so UI Toolkit stops calling this the moment the line
            // leaves the panel - which is what /clear does to the whole log.
            _frameSchedule = _labelBeingAnimated.schedule.Execute(ShowNextFrame).Every(k_frameIntervalMilliseconds);
        }

        void ShowNextFrame()
        {
            _currentFrameIndex = (_currentFrameIndex + 1) % k_spinnerFrames.Length;
            _labelBeingAnimated.text = BuildTextForCurrentFrame();
        }

        string BuildTextForCurrentFrame()
        {
            string currentFrame = k_spinnerFrames[_currentFrameIndex];

            return string.IsNullOrEmpty(_prefixText)
                ? currentFrame
                : _prefixText + k_separatorBeforeTheFrame + currentFrame;
        }

        /// <summary>
        /// Stops the animation and leaves <paramref name="finalText"/> on the line. Safe to call
        /// when nothing is running - a turn can end at any point, including before the first frame.
        /// </summary>
        public void Stop(string finalText)
        {
            var labelToLeaveBehind = _labelBeingAnimated;

            StopAnimatingWithoutTouchingTheText();

            if (labelToLeaveBehind != null)
                labelToLeaveBehind.text = finalText;
        }

        void StopAnimatingWithoutTouchingTheText()
        {
            _frameSchedule?.Pause();
            _frameSchedule = null;
            _labelBeingAnimated = null;
            _prefixText = null;
        }
    }
}
