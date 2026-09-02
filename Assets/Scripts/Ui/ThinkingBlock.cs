using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// One iteration's plan, kept as a single animated line instead of the whole text.
    /// <para>
    /// The model narrates itself before every tool call, and printing that narration in full buried
    /// the actual work: a run of six iterations filled the screen with six paragraphs nobody asked
    /// for. So the text is held here and the line shows "thinking" with a spinner while the plan is
    /// being written, then "thinking  [+]" once it is done. Clicking the line shows the whole plan,
    /// clicking again folds it back - nothing is thrown away, it is just not in the way.
    /// </para>
    /// </summary>
    public class ThinkingBlock
    {
        readonly TerminalView _terminalView;
        readonly Label _lineLabel;
        readonly LineSpinner _lineSpinner = new LineSpinner();

        string _fullThoughtText = string.Empty;
        bool _isExpanded;

        // Carries the [+] from the first frame, not only once the plan is finished. Without it the
        // line spent the whole of a long think looking like a status message rather than something
        // that could be opened, and a model that took a minute to plan looked like a hang with no
        // way to see what it was doing.
        const string k_wordShownWhileThinking = "thinking  [+]";

        const string k_collapsedText = "thinking  [+]";
        const string k_expandedHeader = "thinking  [-]\n";
        const string k_thinkingTextClassName = "line--thinking-text";

        public ThinkingBlock(TerminalView terminalView, Label lineLabel)
        {
            _terminalView = terminalView;
            _lineLabel = lineLabel;

            if (_lineLabel == null) return;

            // The line is a tool card by template, so the thinking class goes on top of it rather
            // than replacing it - the card shape stays, only the colour changes.
            _lineLabel.AddToClassList(k_thinkingTextClassName);

            // Clicks reach the panel because RenderTexturePointerMapper maps the pointer into panel
            // space; without that component this line would look clickable and never respond.
            //
            // The whole CARD listens, not just the label inside it. The label is only as tall as its
            // row of text, so the card's padding - twelve pixels above and below a folded thought -
            // was a dead band wrapped around the smallest target on the screen.
            var elementThatListensForClicks = _lineLabel.parent ?? _lineLabel;
            elementThatListensForClicks.RegisterCallback<ClickEvent>(OnLineClicked);

            _lineSpinner.Start(_lineLabel, k_wordShownWhileThinking);
        }

        /// <summary>
        /// The plan so far. Called on every streaming update with the WHOLE text, never a delta, so
        /// this assigns rather than appends - the same contract the agent loop uses everywhere.
        /// </summary>
        public void SetFullText(string thoughtText)
        {
            _fullThoughtText = thoughtText ?? string.Empty;

            if (_isExpanded)
                ShowWhicheverStateTheBlockIsIn();
        }

        /// <summary>Stops the animation and leaves the line folded. Called once the plan is final,
        /// and again when the run ends, so a cancelled turn never leaves a line spinning.</summary>
        public void Collapse()
        {
            _isExpanded = false;
            ShowWhicheverStateTheBlockIsIn();
        }

        /// <summary>
        /// Takes the whole line back out of the log when the model produced no plan at all. Without
        /// it, an iteration that went straight to a tool call would leave an empty card behind.
        /// </summary>
        public void RemoveIfThereWasNoThought()
        {
            if (!string.IsNullOrWhiteSpace(_fullThoughtText)) return;

            StopTheAnimation();

            if (_terminalView != null)
                _terminalView.RemoveLineContainingLabel(_lineLabel);
        }

        void OnLineClicked(ClickEvent clickEvent)
        {
            _isExpanded = !_isExpanded;
            ShowWhicheverStateTheBlockIsIn();
        }

        // The animation is stopped first and the text written afterwards, because a running spinner
        // rewrites the line every 120 ms and would wipe out whatever was put there.
        void ShowWhicheverStateTheBlockIsIn()
        {
            StopTheAnimation();

            if (_lineLabel == null) return;

            _lineLabel.text = _isExpanded ? k_expandedHeader + _fullThoughtText : k_collapsedText;
        }

        void StopTheAnimation()
        {
            if (_lineSpinner.IsRunning)
                _lineSpinner.Stop(string.Empty);
        }
    }
}
