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
    /// is a plain normalise-and-rescale: the fraction of the screen the pointer is at, times the
    /// size of the texture.
    /// </para>
    /// <para>
    /// THERE IS NO VERTICAL FLIP HERE, and that is the whole point of this comment. Unity screen
    /// coordinates do start at the bottom-left corner, but the position handed to this function has
    /// ALREADY been flipped for us - it arrives measured from the TOP of the screen, in screen
    /// pixels. Flipping it again mirrors every click about the horizontal centre line, and that is
    /// exactly what this component used to do: a line near the top of the log could only be hit by
    /// clicking the same distance from the bottom, pressing the scrollbar dragger threw the view to
    /// the other end of the log, and the wheel did nothing at all whenever the mirrored point fell
    /// outside the scroll view. Measured in play mode: the pointer arrives at y=222 for a click
    /// 222 pixels below the top edge of a 1080-pixel-tall screen.
    /// </para>
    /// <para>
    /// The rescale targets the size of the TEXTURE, not the logical size of the panel, and the
    /// difference is not cosmetic. Unity finishes the conversion itself - <c>ScreenToPanel</c> is
    /// <c>screenToPanelSpace(screen) / panel.scale</c> - so this function owes it TEXTURE PIXELS
    /// and nothing else. Returning panel points instead divides by the scale twice, which pulls
    /// every hit towards the top-left corner by that factor and leaves the bottom of the screen
    /// unreachable on any display whose dpi is not the panel's reference dpi.
    /// </para>
    /// <para>
    /// The panel settings keep the scale at 1 (ConstantPixelSize) so the two spaces coincide
    /// anyway, which is what makes the terminal look the same on every monitor. This still
    /// multiplies by the texture size rather than assuming that, so a later change of scale mode
    /// cannot silently break the mouse again.
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
            float verticalFractionFromTheTop = screenPosition.y / Screen.height;

            return new Vector2(
                horizontalFractionOfTheScreen * targetTexture.width,
                verticalFractionFromTheTop * targetTexture.height);
        }

        // NaN is how UI Toolkit is told the pointer is not over the panel at all. Returning zero
        // instead would park a phantom cursor in the top-left corner and light up whatever is there.
        static Vector2 BuildPositionThatMeansOutsideThePanel()
        {
            return new Vector2(float.NaN, float.NaN);
        }
    }
}
