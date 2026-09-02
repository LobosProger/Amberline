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

        // True from the moment the user presses the scrollbar until they let go. Auto-scrolling is
        // suspended for the whole gesture, because the numbers alone cannot tell a slow drag from
        // the log standing still - see OnPointerDownOnScrollbar.
        bool _isUserDraggingTheScrollbar;

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

            // TrickleDown, so the press is seen before the dragger takes pointer capture. The
            // release is routed to the capturing dragger, which sits inside the scroller, so it
            // trickles through here as well even when the pointer has left the window.
            _scrollView.verticalScroller.RegisterCallback<PointerDownEvent>(OnPointerDownOnScrollbar, TrickleDown.TrickleDown);
            _scrollView.verticalScroller.RegisterCallback<PointerUpEvent>(OnPointerUpOnScrollbar, TrickleDown.TrickleDown);
        }

        void OnDisable()
        {
            if (_scrollView == null) return;

            _scrollView.verticalScroller.valueChanged -= OnVerticalScrollerValueChanged;
            _scrollView.verticalScroller.UnregisterCallback<PointerDownEvent>(OnPointerDownOnScrollbar, TrickleDown.TrickleDown);
            _scrollView.verticalScroller.UnregisterCallback<PointerUpEvent>(OnPointerUpOnScrollbar, TrickleDown.TrickleDown);
        }

        /// <summary>
        /// Puts the view back on the bottom of the log, whatever the user had scrolled to. The
        /// terminal calls this the moment the user submits something: having scrolled up to read,
        /// they would otherwise send a command and watch the echo, the whole answer and every tool
        /// card land somewhere below the fold.
        /// <para>
        /// It only sets the pin and lets <see cref="LateUpdate"/> do the scrolling. Scrolling here
        /// would land one line short every time, because the line being submitted has not been laid
        /// out yet and highValue still describes the log without it.
        /// </para>
        /// </summary>
        public void PinToBottom()
        {
            // A command can be submitted with the scrollbar still held - Enter goes to the input
            // field, not to the dragger. The gesture flag outranks the pin in LateUpdate, so
            // leaving it set would swallow this request whole.
            _isUserDraggingTheScrollbar = false;
            _isPinnedToBottom = true;
        }

        // Runs in LateUpdate so it lands after UI Toolkit has laid out the line that was just
        // added - otherwise highValue is still stale and the view stops one line short.
        void LateUpdate()
        {
            if (_isUserDraggingTheScrollbar) return;
            if (!_isPinnedToBottom) return;

            ScrollToBottom();
        }

        // Pressing the scrollbar is a statement of intent on its own, so the pin is dropped up
        // front rather than inferred afterwards from how far the value moved. Without this, a drag
        // in steps smaller than the tolerance never registered as scrolling up, and LateUpdate
        // pulled the dragger back down to the bottom every frame, out from under the cursor.
        void OnPointerDownOnScrollbar(PointerDownEvent pointerDownEvent)
        {
            _isUserDraggingTheScrollbar = true;
            _isPinnedToBottom = false;
        }

        // Let go at the bottom and the terminal follows the log again. Let go anywhere else and it
        // stays where the user put it.
        void OnPointerUpOnScrollbar(PointerUpEvent pointerUpEvent)
        {
            _isUserDraggingTheScrollbar = false;

            float scrollValueOnRelease = _scrollView.verticalScroller.value;

            _lastKnownScrollValue = scrollValueOnRelease;
            _lastKnownBottomScrollOffset = ResolveBottomScrollOffset();
            _isPinnedToBottom = IsAtBottom(scrollValueOnRelease);
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

            // While the scrollbar is held the pin belongs to the gesture: it went off on the press
            // and is decided again on release, so nothing in between should touch it.
            if (_isUserDraggingTheScrollbar) return;

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

            // Writing the same value back on every single frame costs a notification and a repaint
            // for nothing, and each of those notifications is another chance to be mistaken for
            // something the user did.
            if (Mathf.Abs(_scrollView.scrollOffset.y - bottomScrollOffset) <= k_bottomToleranceInPixels)
            {
                _lastKnownScrollValue = _scrollView.scrollOffset.y;
                _lastKnownBottomScrollOffset = bottomScrollOffset;
                return;
            }

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
