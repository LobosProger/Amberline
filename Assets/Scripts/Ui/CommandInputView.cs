using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Owns the bottom input row: the real <see cref="TextField"/> the user types into, the
    /// rich-text overlay <see cref="Label"/> that draws the blinking cursor and the grayed-out
    /// completion, and the small key hint on the right.
    /// <para>
    /// Raises <see cref="OnCommandSubmitted"/> on Enter and <see cref="OnCancelRequested"/> on
    /// Escape. Tab completes the current suggestion in place, Up and Down walk the history.
    /// The controller locks the row through <see cref="SetInputLocked"/> while a turn is running.
    /// </para>
    /// </summary>
    public class CommandInputView : MonoBehaviour
    {
        /// <summary>Raised when the user submits a non-empty trimmed line with Enter.</summary>
        public event Action<string> OnCommandSubmitted;

        /// <summary>Raised when the user presses Escape - the controller decides what to abort.</summary>
        public event Action OnCancelRequested;

        // The document root, kept so a click anywhere on the terminal can put focus back in
        // the field. Most of the screen is the scrolling log, and clicking it blurs the field.
        VisualElement _documentRootVisualElement;

        TextField _commandTextField;
        Label _overlayLabel;
        Label _keyHintLabel;
        IVisualElementScheduledItem _cursorBlinkScheduler;
        IVisualElementScheduledItem _focusRestoreScheduler;

        // Slash commands the user can complete against. The controller owns the list.
        readonly List<string> _availableCommandsForAutocomplete = new List<string>();

        // Everything submitted this session, oldest first. Up and Down walk it.
        readonly List<string> _submittedCommandHistory = new List<string>();

        // Position inside the history while browsing. Equal to the history count when the user is
        // typing a fresh line rather than looking at an old one.
        int _historyBrowseIndex;

        // Full command currently being suggested, and the tail of it after what has been typed.
        string _currentFullSuggestion;
        string _currentSuggestionRemainder;

        bool _isCursorVisibleInBlinkCycle;
        bool _isInputCurrentlyLocked;
        bool _isTextFieldFocused;

        const int k_cursorBlinkIntervalMs = 500;

        // One frame apart, for a quarter of a second. Long enough to outlast the rebuild that
        // flipping isReadOnly triggers, short enough that it is over before the user can react.
        const int k_focusRestoreIntervalMs = 16;
        const int k_focusRestoreDurationMs = 250;
        const string k_cursorBlockCharacter = "▌";

        // Rich-text tags used to assemble the overlay markup. Kept as constants so the builder
        // methods below stay readable instead of being full of string literals.
        const string k_transparentColorTagOpen = "<color=#00000000>";
        const string k_suggestionColorTagOpen = "<color=#FFFFFF66>";
        const string k_richColorTagClose = "</color>";
        const string k_noparseOpen = "<noparse>";
        const string k_noparseClose = "</noparse>";
        const string k_keyHintHiddenClassName = "input-row__hint--hidden";
        const string k_fieldLockedClassName = "input-row__field--locked";

        void OnEnable()
        {
            QueryUiElementsFromUiDocument();
            InitializeOverlayLabel();
            RegisterTextFieldCallbacks();

            StartCursorBlinking();
            RefreshOverlayLabel();
            RefreshKeyHintVisibility();

            RestoreFocusToTextFieldUntilItTakes();
        }

        void OnDisable()
        {
            UnregisterTextFieldCallbacks();
            StopCursorBlinking();
            StopFocusRestoring();
        }

        void QueryUiElementsFromUiDocument()
        {
            _documentRootVisualElement = GetComponent<UIDocument>().rootVisualElement;
            _commandTextField = _documentRootVisualElement.Q<TextField>(className: "input-row__field");
            _overlayLabel = _documentRootVisualElement.Q<Label>("input-row__overlay");
            _keyHintLabel = _documentRootVisualElement.Q<Label>("input-row__hint");
        }

        void InitializeOverlayLabel()
        {
            _overlayLabel.enableRichText = true;
            _overlayLabel.text = string.Empty;
        }

        // Everything is registered on TrickleDown so Tab, Enter and the arrow keys are intercepted
        // before the default TextField handlers run. Stopping a key event does not suppress the
        // navigation event UI Toolkit builds beside it, so both kinds still have to be answered -
        // but only one of them acts.
        //
        // The navigation callbacks are SUPPRESSORS now, not handlers. The scene's EventSystem has
        // Send Navigation Events switched off, because the input module builds those events from the
        // Navigate action, whose template binds W, A, S and D to the same four directions as the
        // arrow keys - so a typed "w" arrived as Direction.Up and silently replaced the line the
        // user was writing with an entry from the history. A navigation event carries no key, so no
        // handler can tell the two apart; the only fix is not to act on direction at all. Every key
        // this terminal binds is handled on KeyDownEvent, which the panel sends regardless.
        //
        // The pointer callback goes on the DOCUMENT ROOT rather than the field, because its whole
        // job is to catch the clicks the field never sees. TrickleDown, so it still runs for the
        // scroll view and the scrollbar, both of which stop the event on their way back up.
        void RegisterTextFieldCallbacks()
        {
            _documentRootVisualElement.RegisterCallback<PointerDownEvent>(OnPointerDownAnywhereInTerminal, TrickleDown.TrickleDown);
            _commandTextField.RegisterCallback<KeyDownEvent>(OnTextFieldKeyDown, TrickleDown.TrickleDown);
            _commandTextField.RegisterCallback<NavigationMoveEvent>(OnTextFieldNavigationMove, TrickleDown.TrickleDown);
            _commandTextField.RegisterCallback<NavigationSubmitEvent>(OnTextFieldNavigationSubmit, TrickleDown.TrickleDown);
            _commandTextField.RegisterCallback<NavigationCancelEvent>(OnTextFieldNavigationCancel, TrickleDown.TrickleDown);
            _commandTextField.RegisterCallback<FocusInEvent>(OnTextFieldFocusIn);
            _commandTextField.RegisterCallback<FocusOutEvent>(OnTextFieldFocusOut);
            _commandTextField.RegisterValueChangedCallback(OnTextFieldValueChanged);
        }

        void UnregisterTextFieldCallbacks()
        {
            _documentRootVisualElement.UnregisterCallback<PointerDownEvent>(OnPointerDownAnywhereInTerminal, TrickleDown.TrickleDown);
            _commandTextField.UnregisterCallback<KeyDownEvent>(OnTextFieldKeyDown, TrickleDown.TrickleDown);
            _commandTextField.UnregisterCallback<NavigationMoveEvent>(OnTextFieldNavigationMove, TrickleDown.TrickleDown);
            _commandTextField.UnregisterCallback<NavigationSubmitEvent>(OnTextFieldNavigationSubmit, TrickleDown.TrickleDown);
            _commandTextField.UnregisterCallback<NavigationCancelEvent>(OnTextFieldNavigationCancel, TrickleDown.TrickleDown);
            _commandTextField.UnregisterCallback<FocusInEvent>(OnTextFieldFocusIn);
            _commandTextField.UnregisterCallback<FocusOutEvent>(OnTextFieldFocusOut);
            _commandTextField.UnregisterValueChangedCallback(OnTextFieldValueChanged);
        }

        /// <summary>
        /// Locks or unlocks the row. While locked the overlay is hidden and the field cannot be
        /// edited; unlocking refocuses the field so the user can type again straight away.
        /// </summary>
        public void SetInputLocked(bool isLocked)
        {
            _isInputCurrentlyLocked = isLocked;

            // Read-only rather than disabled: a disabled element loses focus and stops receiving
            // key events, which would silently kill the Escape-to-cancel the hint promises. The
            // submit and history paths check the lock themselves instead.
            _commandTextField.isReadOnly = isLocked;
            _commandTextField.EnableInClassList(k_fieldLockedClassName, isLocked);

            if (isLocked)
            {
                _overlayLabel.style.display = DisplayStyle.None;

                // Read-only is only half of what the comment above promises: flipping it rebuilds
                // the field's inner text element and drops the focus with it, and a blurred field
                // hears no Escape. Asking for the focus back is the other half.
                RestoreFocusToTextFieldUntilItTakes();
                RefreshKeyHintVisibility();
                return;
            }

            _overlayLabel.style.display = DisplayStyle.Flex;
            RestoreFocusToTextFieldUntilItTakes();
            RefreshOverlayLabel();
            RefreshKeyHintVisibility();
        }

        /// <summary>Replaces the pool of commands Tab can complete against.</summary>
        public void SetAvailableCommandsForAutocomplete(IEnumerable<string> availableCommands)
        {
            _availableCommandsForAutocomplete.Clear();

            if (availableCommands != null)
                _availableCommandsForAutocomplete.AddRange(availableCommands);

            RecalculateSuggestionForTypedText(_commandTextField.value);
            RefreshOverlayLabel();
        }

        // Focus has to be scheduled rather than called directly: right after the field is enabled
        // or the panel is built, Focus() silently no-ops because the element is still being set up.
        //
        // And it has to be re-checked rather than set once. Every caller here runs while something
        // else is about to take the focus away - a lock flipping isReadOnly, a click landing on the
        // log - and a single scheduled Focus() is simply overwritten by whichever of those runs
        // last. So this keeps asking for a few frames and stops the moment the field really has it.
        void RestoreFocusToTextFieldUntilItTakes()
        {
            StopFocusRestoring();

            _focusRestoreScheduler = _commandTextField.schedule
                .Execute(FocusTextFieldIfItIsNotFocusedYet)
                .Every(k_focusRestoreIntervalMs)
                .ForDuration(k_focusRestoreDurationMs);
        }

        void FocusTextFieldIfItIsNotFocusedYet()
        {
            if (IsTextFieldHoldingFocus())
            {
                StopFocusRestoring();
                return;
            }

            _commandTextField.Focus();
        }

        // A TextField hands its focus to an inner text element, so the focused element is a CHILD
        // of the field rather than the field itself - comparing the two by reference never matches.
        bool IsTextFieldHoldingFocus()
        {
            var focusController = _commandTextField.focusController;

            if (focusController == null) return false;

            return focusController.focusedElement is VisualElement focusedElement
                   && _commandTextField.Contains(focusedElement);
        }

        void StopFocusRestoring()
        {
            if (_focusRestoreScheduler == null) return;

            _focusRestoreScheduler.Pause();
            _focusRestoreScheduler = null;
        }

        void StartCursorBlinking()
        {
            _isCursorVisibleInBlinkCycle = true;
            _cursorBlinkScheduler = _overlayLabel.schedule.Execute(ToggleCursorVisibilityInBlinkCycle).Every(k_cursorBlinkIntervalMs);
        }

        void StopCursorBlinking()
        {
            if (_cursorBlinkScheduler == null) return;

            _cursorBlinkScheduler.Pause();
            _cursorBlinkScheduler = null;
        }

        void ToggleCursorVisibilityInBlinkCycle()
        {
            _isCursorVisibleInBlinkCycle = !_isCursorVisibleInBlinkCycle;
            RefreshOverlayLabel();
        }

        // A terminal is one input surface: clicking the log, the top bar or the scrollbar should
        // never leave the user with nowhere to type. The test is WHERE the press landed, not whether
        // the field is focused right now - this runs before the press has had its effect, so at this
        // moment the field it is about to blur still holds focus.
        //
        // A press inside the field is left alone, so clicking halfway along a typed line still puts
        // the caret there instead of jumping it to the end.
        void OnPointerDownAnywhereInTerminal(PointerDownEvent pointerDownEvent)
        {
            if (_commandTextField.Contains(pointerDownEvent.target as VisualElement)) return;

            RestoreFocusToTextFieldUntilItTakes();
        }

        void OnTextFieldFocusIn(FocusInEvent focusInEvent)
        {
            _isTextFieldFocused = true;

            // Reset the blink so the cursor appears the moment the field is clicked.
            _isCursorVisibleInBlinkCycle = true;
            RefreshOverlayLabel();
            RefreshKeyHintVisibility();
        }

        void OnTextFieldFocusOut(FocusOutEvent focusOutEvent)
        {
            _isTextFieldFocused = false;
            RefreshOverlayLabel();
            RefreshKeyHintVisibility();
        }

        // The hint only means anything while the field is focused and unlocked. A USS class is
        // toggled instead of style.display so the layout keeps reserving the same space and the
        // input row does not jump when the hint appears or disappears.
        void RefreshKeyHintVisibility()
        {
            if (_keyHintLabel == null) return;

            bool shouldShowKeyHint = _isTextFieldFocused && !_isInputCurrentlyLocked;

            if (shouldShowKeyHint)
                _keyHintLabel.RemoveFromClassList(k_keyHintHiddenClassName);
            else
                _keyHintLabel.AddToClassList(k_keyHintHiddenClassName);
        }

        void OnTextFieldValueChanged(ChangeEvent<string> changeEvent)
        {
            RecalculateSuggestionForTypedText(changeEvent.newValue);

            // Typing means the user is composing a fresh line, so stop browsing the history.
            _historyBrowseIndex = _submittedCommandHistory.Count;

            _isCursorVisibleInBlinkCycle = true;
            RefreshOverlayLabel();
        }

        // Picks the best completion for the typed prefix and stores both the full command (for
        // Tab) and the tail after the prefix (for the gray ghost text).
        void RecalculateSuggestionForTypedText(string typedText)
        {
            _currentFullSuggestion = FindBestMatchingCommandFor(typedText);

            if (string.IsNullOrEmpty(_currentFullSuggestion) || string.IsNullOrEmpty(typedText))
            {
                _currentSuggestionRemainder = string.Empty;
                return;
            }

            _currentSuggestionRemainder = _currentFullSuggestion.Substring(typedText.Length);
        }

        // Returns the first command that starts with typedText and is strictly longer than it,
        // so there is always something left to suggest. List order decides priority.
        string FindBestMatchingCommandFor(string typedText)
        {
            if (string.IsNullOrWhiteSpace(typedText)) return null;

            foreach (var availableCommand in _availableCommandsForAutocomplete)
            {
                bool isPrefixMatch = availableCommand.StartsWith(typedText, StringComparison.OrdinalIgnoreCase);
                bool hasRemainderToSuggest = availableCommand.Length > typedText.Length;

                if (isPrefixMatch && hasRemainderToSuggest)
                    return availableCommand;
            }

            return null;
        }

        // Rebuilds the overlay from three independent pieces: a transparent copy of the typed
        // text (so the cursor lines up with the real caret), the blinking cursor block, and the
        // translucent tail of the current suggestion.
        void RefreshOverlayLabel()
        {
            if (_isInputCurrentlyLocked || _overlayLabel == null) return;

            _overlayLabel.text = BuildTransparentTypedTextMarkup(_commandTextField.value)
                               + BuildBlinkingCursorMarkup()
                               + BuildSuggestionRemainderMarkup();
        }

        string BuildTransparentTypedTextMarkup(string typedText)
        {
            if (string.IsNullOrEmpty(typedText)) return string.Empty;

            return $"{k_transparentColorTagOpen}{k_noparseOpen}{typedText}{k_noparseClose}{k_richColorTagClose}";
        }

        // Either the visible cursor block or a transparent one of the same width, so the
        // suggestion after it does not jitter every time the cursor blinks.
        string BuildBlinkingCursorMarkup()
        {
            return _isCursorVisibleInBlinkCycle
                ? k_cursorBlockCharacter
                : $"{k_transparentColorTagOpen}{k_cursorBlockCharacter}{k_richColorTagClose}";
        }

        string BuildSuggestionRemainderMarkup()
        {
            if (string.IsNullOrEmpty(_currentSuggestionRemainder)) return string.Empty;

            return $"{k_suggestionColorTagOpen}{k_noparseOpen}{_currentSuggestionRemainder}{k_noparseClose}{k_richColorTagClose}";
        }

        void OnTextFieldKeyDown(KeyDownEvent keyDownEvent)
        {
            if (keyDownEvent.keyCode == KeyCode.Tab)
            {
                TryApplyCurrentSuggestionToTextField();

                // Stopped even when there was nothing to complete. Letting Tab through hands it to
                // the default focus ring, which walks focus out of the input row - the user then
                // types into nothing and has to click their way back.
                keyDownEvent.StopPropagation();
                return;
            }

            if (keyDownEvent.keyCode == KeyCode.Escape)
            {
                RaiseCancelRequested();
                keyDownEvent.StopPropagation();
                return;
            }

            if (keyDownEvent.keyCode == KeyCode.UpArrow)
            {
                if (TryStepThroughHistory(-1))
                    keyDownEvent.StopPropagation();

                return;
            }

            if (keyDownEvent.keyCode == KeyCode.DownArrow)
            {
                if (TryStepThroughHistory(1))
                    keyDownEvent.StopPropagation();

                return;
            }

            // Enter arrives as Return, KeypadEnter, or a bare newline character depending on the
            // event phase, so all three have to be accepted.
            bool isEnterKeyPressed = keyDownEvent.keyCode == KeyCode.Return
                || keyDownEvent.keyCode == KeyCode.KeypadEnter
                || keyDownEvent.character == '\n'
                || keyDownEvent.character == '\r';

            if (!isEnterKeyPressed) return;

            if (TrySubmitTypedCommand())
                keyDownEvent.StopPropagation();
        }

        void OnTextFieldNavigationMove(NavigationMoveEvent navigationMoveEvent)
        {
            // Tab is delivered as Next / Previous. Intercept it so it completes instead of
            // moving focus out of the input row.
            if (navigationMoveEvent.direction == NavigationMoveEvent.Direction.Next
                || navigationMoveEvent.direction == NavigationMoveEvent.Direction.Previous)
            {
                TryApplyCurrentSuggestionToTextField();

                // Always stopped, for the same reason as Tab on the key path above.
                navigationMoveEvent.StopPropagation();
                return;
            }

            // Up, Down, Left and Right are swallowed and nothing more. The history lives on the
            // key path, where the actual arrow key is visible; acting on the direction here is what
            // made typing "w" jump through the history. Swallowing them still keeps the promise of
            // the row: a direction can never walk focus out of the input field.
            navigationMoveEvent.StopPropagation();
        }

        void OnTextFieldNavigationSubmit(NavigationSubmitEvent navigationSubmitEvent)
        {
            if (TrySubmitTypedCommand())
                navigationSubmitEvent.StopPropagation();
        }

        void OnTextFieldNavigationCancel(NavigationCancelEvent navigationCancelEvent)
        {
            RaiseCancelRequested();
            navigationCancelEvent.StopPropagation();
        }

        // Clears the field, records the line in the history, and notifies the controller.
        // Returns false when there is nothing to submit.
        bool TrySubmitTypedCommand()
        {
            if (_isInputCurrentlyLocked) return false;

            var submittedText = _commandTextField.value;
            if (string.IsNullOrWhiteSpace(submittedText)) return false;

            var trimmedCommand = submittedText.Trim();

            _submittedCommandHistory.Add(trimmedCommand);
            _historyBrowseIndex = _submittedCommandHistory.Count;

            SetTextFieldValueSilently(string.Empty);

            // The controller locks the row inside this call, synchronously, which is what takes the
            // focus away - so the focus is asked for AFTER it, never before. A Focus() placed above
            // this line is undone a microsecond later and reads like it works until you try to type.
            OnCommandSubmitted?.Invoke(trimmedCommand);

            RestoreFocusToTextFieldUntilItTakes();
            return true;
        }

        // Moves through the history by one step: -1 towards older entries, +1 towards newer ones.
        // Stepping past the newest entry lands on an empty line, which is the draft slot.
        bool TryStepThroughHistory(int stepDirection)
        {
            if (_isInputCurrentlyLocked) return false;
            if (_submittedCommandHistory.Count == 0) return false;

            int requestedIndex = _historyBrowseIndex + stepDirection;
            if (requestedIndex < 0) requestedIndex = 0;
            if (requestedIndex > _submittedCommandHistory.Count) requestedIndex = _submittedCommandHistory.Count;

            if (requestedIndex == _historyBrowseIndex) return false;

            _historyBrowseIndex = requestedIndex;

            bool isOnTheDraftSlot = _historyBrowseIndex == _submittedCommandHistory.Count;
            SetTextFieldValueSilently(isOnTheDraftSlot ? string.Empty : _submittedCommandHistory[_historyBrowseIndex]);

            return true;
        }

        bool TryApplyCurrentSuggestionToTextField()
        {
            if (_isInputCurrentlyLocked) return false;
            if (string.IsNullOrEmpty(_currentFullSuggestion)) return false;

            SetTextFieldValueSilently(_currentFullSuggestion);
            return true;
        }

        // Writes into the field without raising ChangeEvent, then brings the caret, the
        // suggestion and the overlay back in sync by hand. Going through SetValueWithoutNotify
        // keeps the history browser from treating its own writes as the user typing.
        void SetTextFieldValueSilently(string newValue)
        {
            _commandTextField.SetValueWithoutNotify(newValue);
            _commandTextField.cursorIndex = newValue.Length;
            _commandTextField.selectIndex = newValue.Length;

            RecalculateSuggestionForTypedText(newValue);
            _isCursorVisibleInBlinkCycle = true;
            RefreshOverlayLabel();
        }

        void RaiseCancelRequested()
        {
            OnCancelRequested?.Invoke();
        }
    }
}
