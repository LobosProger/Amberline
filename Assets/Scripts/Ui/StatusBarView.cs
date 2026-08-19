using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Owns the thin bar under the input row: the full workspace path on the left and the
    /// current agent state on the right. Purely a display - it never decides what the state is.
    /// </summary>
    public class StatusBarView : MonoBehaviour
    {
        Label _workspacePathLabel;
        Label _stateLabel;

        const string k_workspaceElementName = "status-bar__workspace";
        const string k_stateElementName = "status-bar__state";
        const string k_noWorkspaceText = "workspace: none";

        void OnEnable()
        {
            var rootVisualElement = GetComponent<UIDocument>().rootVisualElement;
            _workspacePathLabel = rootVisualElement.Q<Label>(k_workspaceElementName);
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
    }
}
