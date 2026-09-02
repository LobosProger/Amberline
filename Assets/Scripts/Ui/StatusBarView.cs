using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Owns the thin bar under the input row: the full workspace path on the left, then the
    /// generation speed, then the current agent state. Purely a display - it never decides what
    /// the state is or how fast anything is going.
    /// </summary>
    /// <remarks>
    /// The speed has a label of its own rather than sharing the state one. The state label already
    /// carries the state word and the context budget, written whole by a single caller, and a
    /// second writer appending to the same string would simply overwrite the first.
    /// </remarks>
    public class StatusBarView : MonoBehaviour
    {
        Label _workspacePathLabel;
        Label _generationSpeedLabel;
        Label _stateLabel;

        const string k_workspaceElementName = "status-bar__workspace";
        const string k_generationSpeedElementName = "status-bar__speed";
        const string k_stateElementName = "status-bar__state";
        const string k_noWorkspaceText = "workspace: none";

        void OnEnable()
        {
            var rootVisualElement = GetComponent<UIDocument>().rootVisualElement;
            _workspacePathLabel = rootVisualElement.Q<Label>(k_workspaceElementName);
            _generationSpeedLabel = rootVisualElement.Q<Label>(k_generationSpeedElementName);
            _stateLabel = rootVisualElement.Q<Label>(k_stateElementName);
        }

        /// <summary>Shows the absolute path the agent is sandboxed to.</summary>
        public void SetWorkspacePath(string workspacePath)
        {
            if (_workspacePathLabel == null) return;

            _workspacePathLabel.text = string.IsNullOrEmpty(workspacePath)
                ? k_noWorkspaceText
                : $"workspace: {workspacePath}";
        }

        /// <summary>Shows what the agent is doing right now - "idle", "thinking", "cancelled".</summary>
        public void SetState(string stateText)
        {
            if (_stateLabel == null) return;

            _stateLabel.text = stateText;
        }

        /// <summary>
        /// Shows how fast the model is generating. Pass an empty string to clear it.
        /// </summary>
        public void SetGenerationSpeed(string generationSpeedText)
        {
            if (_generationSpeedLabel == null) return;

            _generationSpeedLabel.text = generationSpeedText ?? string.Empty;
        }
    }
}
