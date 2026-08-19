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

        TextField _commandTextField;
        Label _overlayLabel;
        Label _keyHintLabel;
        IVisualElementScheduledItem _cursorBlinkScheduler;

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

            FocusTextFieldOnNextFrame();
        }

        void OnDisable()
        {
            UnregisterTextFieldCallbacks();
            StopCursorBlinking();
        }

        void QueryUiElementsFromUiDocument()
        {
            var rootVisualElement = GetComponent<UIDocument>().rootVisualElement;
            _commandTextField = rootVisualElement.Q<TextField>(className: "input-row__field");
            _overlayLabel = rootVisualElement.Q<Label>("input-row__overlay");
            _keyHintLabel = rootVisualElement.Q<Label>("input-row__hint");
        }

        void InitializeOverlayLabel()
        {
            _overlayLabel.enableRichText = true;
            _overlayLabel.text = string.Empty;
        }

        // KeyDown, NavigationMove and NavigationSubmit are registered on TrickleDown so Tab,
        // Enter and the arrow keys are intercepted before the default TextField handlers run.
        // UI Toolkit synthesises the navigation events independently of the key event, so
        // stopping one does not suppress the other - both have to be handled.
        void RegisterTextFieldCallbacks()
        {
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
                RefreshKeyHintVisibility();
                return;
            }

            _overlayLabel.style.display = DisplayStyle.Flex;
            FocusTextFieldOnNextFrame();
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
        void FocusTextFieldOnNextFrame()
        {
            _commandTextField.schedule.Execute(() => _commandTextField.Focus());
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
                if (TryApplyCurrentSuggestionToTextField())
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
                if (TryApplyCurrentSuggestionToTextField())
                    navigationMoveEvent.StopPropagation();

                return;
            }

            if (navigationMoveEvent.direction == NavigationMoveEvent.Direction.Up)
            {
                if (TryStepThroughHistory(-1))
                    navigationMoveEvent.StopPropagation();

                return;
            }

            if (navigationMoveEvent.direction == NavigationMoveEvent.Direction.Down)
            {
                if (TryStepThroughHistory(1))
                    navigationMoveEvent.StopPropagation();
            }
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
            _commandTextField.Focus();

            OnCommandSubmitted?.Invoke(trimmedCommand);
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
