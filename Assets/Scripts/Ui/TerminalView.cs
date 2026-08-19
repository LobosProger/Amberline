using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Renders the scrolling terminal log: instantiates the UXML template that matches each
    /// <see cref="TerminalLineKind"/> and, for boot text, types it out character by character.
    /// Holds no agent state - it only turns calls from the controller into visual elements.
    /// </summary>
    public class TerminalView : MonoBehaviour
    {
        [Header("Line Templates")]
        [SerializeField] VisualTreeAsset _bootLineTemplate;
        [SerializeField] VisualTreeAsset _bannerBoxTemplate;
        [SerializeField] VisualTreeAsset _commandEchoTemplate;
        [Space]
        [SerializeField] VisualTreeAsset _responseBoxTemplate;
        [SerializeField] VisualTreeAsset _toolCardTemplate;
        [SerializeField] VisualTreeAsset _errorBoxTemplate;

        VisualElement _terminalContent;

        // Every container currently in the log, oldest first. Kept so the view can drop the
        // oldest entries once the log grows past the budget below.
        readonly List<VisualElement> _appendedLineContainers = new List<VisualElement>();

        // Set to true when the user presses Space during the boot animation. The animator polls
        // this on every character and bails out, leaving the full text on screen.
        bool _isSkipRequestedForCurrentAnimation;

        // A long session would otherwise keep thousands of live elements laid out at all times.
        // Note this counts entries, not wrapped text lines: one entry holding a large diff still
        // counts as one. That is fine while nothing renders diffs; revisit when DiffView lands.
        const int k_maxAppendedLineContainers = 400;
        const float k_defaultCharacterDelaySeconds = 0.012f;
        const string k_textElementName = "text";

        void OnEnable()
        {
            _terminalContent = GetComponent<UIDocument>().rootVisualElement.Q<VisualElement>("terminal-content");
        }

        /// <summary>
        /// Lets the user skip whatever is currently being typed out. The controller calls this
        /// instead of the view polling the keyboard, so input stays in one place.
        /// </summary>
        public void RequestSkipOfCurrentAnimation()
        {
            _isSkipRequestedForCurrentAnimation = true;
        }

        /// <summary>
        /// Appends a line and types the text out character by character. Awaits until the
        /// animation finishes, is skipped, or is cancelled.
        /// </summary>
        public async UniTask AppendLineAsync(string text, TerminalLineKind lineKind, CancellationToken cancellationToken, float characterDelaySeconds = k_defaultCharacterDelaySeconds)
        {
            var lineLabel = CreateLineElement(lineKind);

            // Reset the skip flag for the new animation - a Space press that skipped the previous
            // line must not auto-skip this one as well.
            _isSkipRequestedForCurrentAnimation = false;

            await TerminalTextAnimator.TypeTextWithBlinkingCursorAsync(
                lineLabel,
                text,
                characterDelaySeconds,
                cancellationToken,
                isSkipRequested: () => _isSkipRequestedForCurrentAnimation);
        }

        /// <summary>
        /// Appends a line that is shown immediately, with no typing animation. Used for echoing
        /// what the user submitted and for anything the agent has already finished producing.
        /// </summary>
        public void AppendLineInstant(string text, TerminalLineKind lineKind)
        {
            var lineLabel = CreateLineElement(lineKind);
            lineLabel.text = text;
        }

        /// <summary>
        /// Appends an empty line and hands back its label so the caller can keep writing into it
        /// as text streams in. Streaming callers assign the whole cumulative text every time -
        /// they never append to what is already there.
        /// </summary>
        public Label BeginStreamingLine(TerminalLineKind lineKind)
        {
            var lineLabel = CreateLineElement(lineKind);
            lineLabel.text = string.Empty;
            return lineLabel;
        }

        /// <summary>Removes every line from the log. Backs the /clear command.</summary>
        public void ClearAllLines()
        {
            _terminalContent.Clear();
            _appendedLineContainers.Clear();
        }

        // Instantiates the template for the kind, attaches it to the scrolling content, trims the
        // log if it has grown too long, and returns the inner Label for the caller to write into.
        Label CreateLineElement(TerminalLineKind lineKind)
        {
            var template = ResolveTemplate(lineKind);
            var lineContainer = template.Instantiate();
            var lineLabel = lineContainer.Q<Label>(k_textElementName);

            _terminalContent.Add(lineContainer);
            _appendedLineContainers.Add(lineContainer);

            RemoveOldestLinesOverBudget();

            return lineLabel;
        }

        VisualTreeAsset ResolveTemplate(TerminalLineKind lineKind)
        {
            return lineKind switch
            {
                TerminalLineKind.Boot         => _bootLineTemplate,
                TerminalLineKind.Banner       => _bannerBoxTemplate,
                TerminalLineKind.UserCommand  => _commandEchoTemplate,
                TerminalLineKind.AgentMessage => _responseBoxTemplate,
                TerminalLineKind.ToolActivity => _toolCardTemplate,
                TerminalLineKind.Notice       => _bootLineTemplate,
                TerminalLineKind.Error        => _errorBoxTemplate,
                _ => _bootLineTemplate
            };
        }

        void RemoveOldestLinesOverBudget()
        {
            while (_appendedLineContainers.Count > k_maxAppendedLineContainers)
            {
                _appendedLineContainers[0].RemoveFromHierarchy();
                _appendedLineContainers.RemoveAt(0);
            }
        }
    }
}
