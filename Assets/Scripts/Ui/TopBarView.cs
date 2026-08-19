using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Owns the terminal top bar. Shows the folder the agent is currently allowed to work in,
    /// so the sandbox root is visible at a glance instead of being an invisible setting.
    /// </summary>
    public class TopBarView : MonoBehaviour
    {
        Label _workspaceLabel;

        const string k_workspaceElementName = "top-bar__workspace";
        const string k_noWorkspaceText = "[NO WORKSPACE]";

        void OnEnable()
        {
            _workspaceLabel = GetComponent<UIDocument>().rootVisualElement.Q<Label>(k_workspaceElementName);
        }

        /// <summary>
        /// Renders the last path segment of <paramref name="workspaceFolderPath"/> in the top-right
        /// tag. Only the folder name is shown here because the full path already has its own slot
        /// in the status bar.
        /// </summary>
        public void SetWorkspaceFolderPath(string workspaceFolderPath)
        {
            if (_workspaceLabel == null) return;

            if (string.IsNullOrEmpty(workspaceFolderPath))
            {
                _workspaceLabel.text = k_noWorkspaceText;
                return;
            }

            string folderName = new DirectoryInfo(workspaceFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
            _workspaceLabel.text = $"[{folderName.ToUpperInvariant()}]";
        }
    }
}
