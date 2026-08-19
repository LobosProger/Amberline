using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Keeps the terminal <see cref="ScrollView"/> pinned to the bottom as new lines arrive, and
    /// releases the pin as soon as the user scrolls up to read something. Scrolling back down to
    /// the bottom re-pins, which is what every terminal does and what people expect.
    /// </summary>
    public class TerminalAutoScrollView : MonoBehaviour
    {
        ScrollView _scrollView;

        bool _isPinnedToBottom = true;

        // Last scroll value this component saw. Only a decrease counts as the user scrolling up.
        float _lastKnownScrollValue;

        // How far down the log could be scrolled when this component last looked. Needed because a
        // log that gets SHORTER drags the scroll value down with it, and that must not be read as
        // a gesture - see OnVerticalScrollerValueChanged.
        float _lastKnownBottomScrollOffset;

        // Set while this component is writing scrollOffset itself, so the immediate part of the
        // resulting notification is ignored. It is not enough on its own - see below.
        bool _isApplyingAutoScroll;

        // The scroller works in pixels, so "at the bottom" needs a tolerance rather than an
        // exact comparison - layout rounding leaves the value a fraction short of highValue.
        const float k_bottomToleranceInPixels = 2f;

        void OnEnable()
        {
            _scrollView = GetComponent<UIDocument>().rootVisualElement.Q<ScrollView>();
            _scrollView.verticalScroller.valueChanged += OnVerticalScrollerValueChanged;
        }

        void OnDisable()
        {
            if (_scrollView == null) return;

            _scrollView.verticalScroller.valueChanged -= OnVerticalScrollerValueChanged;
        }

        // Runs in LateUpdate so it lands after UI Toolkit has laid out the line that was just
        // added - otherwise highValue is still stale and the view stops one line short.
        void LateUpdate()
        {
            if (!_isPinnedToBottom) return;

            ScrollToBottom();
        }

        // Comparing the new value against highValue is not enough to tell a user gesture from
        // the log simply getting longer: adding a line raises highValue, and the notification
        // that follows still carries the previous value, which then looks exactly like someone
        // scrolling up. Only an actual decrease is a scroll up, and growth can never produce one.
        //
        // A log that gets SHORTER produces one too, and it is not a gesture either. UI Toolkit
        // clamps the scroll value into the smaller range itself, so the notification arrives as a
        // large decrease that nobody asked for. That happens twice in this product: /clear empties
        // the log, and an approval card loses its row of keys the moment it is answered. Left
        // unhandled it unpins the terminal for the rest of the session, and every line after the
        // first approval lands below the fold.
        void OnVerticalScrollerValueChanged(float newScrollValue)
        {
            if (_isApplyingAutoScroll) return;

            float bottomScrollOffset = ResolveBottomScrollOffset();
            bool didTheLogGetShorter = bottomScrollOffset < _lastKnownBottomScrollOffset - k_bottomToleranceInPixels;
            bool didValueDecrease = newScrollValue < _lastKnownScrollValue - k_bottomToleranceInPixels;

            _lastKnownScrollValue = newScrollValue;
            _lastKnownBottomScrollOffset = bottomScrollOffset;

            if (didValueDecrease && !didTheLogGetShorter)
            {
                _isPinnedToBottom = false;
                return;
            }

            if (IsAtBottom(newScrollValue))
                _isPinnedToBottom = true;
        }

        bool IsAtBottom(float scrollValue)
        {
            return scrollValue >= ResolveBottomScrollOffset() - k_bottomToleranceInPixels;
        }

        void ScrollToBottom()
        {
            var bottomScrollOffset = ResolveBottomScrollOffset();

            _isApplyingAutoScroll = true;
            _scrollView.scrollOffset = new Vector2(0, bottomScrollOffset);
            _isApplyingAutoScroll = false;

            _lastKnownScrollValue = bottomScrollOffset;
            _lastKnownBottomScrollOffset = bottomScrollOffset;
        }

        // While the log is still shorter than the view, the scroller reports a negative
        // highValue - the amount of slack left. Scrolling to it would push the whole log down
        // by that many pixels and leave a gap under the top bar, so the offset is floored at
        // zero and only becomes meaningful once the content actually overflows.
        float ResolveBottomScrollOffset()
        {
            return Mathf.Max(0f, _scrollView.verticalScroller.highValue);
        }
    }
}
