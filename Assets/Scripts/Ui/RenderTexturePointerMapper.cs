using UnityEngine;
using UnityEngine.UIElements;

namespace Amberline.Ui
{
    /// <summary>
    /// Teaches the panel where the mouse is. A UI Toolkit panel that draws into a RENDER TEXTURE
    /// has no idea how that texture reaches the screen, so Unity hands it the raw screen position
    /// and every click lands in the wrong place - usually far enough away that nothing looks
    /// clickable at all. <c>PanelSettings.SetScreenToPanelSpaceFunction</c> is the hook for
    /// supplying the missing mapping, and this component is the only place it is set.
    /// <para>
    /// The terminal's texture is stretched across the whole screen by one RawImage, so the mapping
    /// is a plain normalise-and-rescale plus a vertical flip: Unity screen coordinates start at the
    /// bottom-left corner and panel coordinates start at the top-left one.
    /// </para>
    /// <para>
    /// The CRT pass warps the picture slightly after this runs, so a click near the edge of the
    /// screen still lands a few pixels away from where it looks like it should. That is one of the
    /// reasons every clickable thing in this product also has a key.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class RenderTexturePointerMapper : MonoBehaviour
    {
        PanelSettings _panelSettings;

        void OnEnable()
        {
            _panelSettings = GetComponent<UIDocument>().panelSettings;

            if (_panelSettings == null)
            {
                Debug.LogWarning("[RenderTexturePointerMapper] The UI document has no panel settings, so clicks cannot be mapped.");
                return;
            }

            if (_panelSettings.targetTexture == null)
            {
                // Nothing to map: the panel draws straight to the screen and Unity's own identity
                // mapping is already correct.
                return;
            }

            _panelSettings.SetScreenToPanelSpaceFunction(ConvertScreenPositionToPanelPosition);
        }

        void OnDisable()
        {
            if (_panelSettings == null) return;

            // PanelSettings is a shared ASSET, so the function outlives Play mode and would be left
            // pointing at a destroyed component. Passing null puts Unity's default back.
            _panelSettings.SetScreenToPanelSpaceFunction(null);
            _panelSettings = null;
        }

        Vector2 ConvertScreenPositionToPanelPosition(Vector2 screenPosition)
        {
            var targetTexture = _panelSettings == null ? null : _panelSettings.targetTexture;

            if (targetTexture == null || Screen.width <= 0 || Screen.height <= 0)
            {
                return BuildPositionThatMeansOutsideThePanel();
            }

            float horizontalFractionOfTheScreen = screenPosition.x / Screen.width;
            float verticalFractionFromTheBottom = screenPosition.y / Screen.height;

            return new Vector2(
                horizontalFractionOfTheScreen * targetTexture.width,
                (1f - verticalFractionFromTheBottom) * targetTexture.height);
        }

        // NaN is how UI Toolkit is told the pointer is not over the panel at all. Returning zero
        // instead would park a phantom cursor in the top-left corner and light up whatever is there.
        static Vector2 BuildPositionThatMeansOutsideThePanel()
        {
            return new Vector2(float.NaN, float.NaN);
        }
    }
}
