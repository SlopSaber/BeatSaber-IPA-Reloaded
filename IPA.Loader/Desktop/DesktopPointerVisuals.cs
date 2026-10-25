using System;
using UnityEngine;
using UnityEngine.UI;
using Logger = IPA.Logging.Logger;
using Object = UnityEngine.Object;

namespace IPA.Desktop
{
    internal sealed class DesktopPointerVisuals : IDisposable
    {
        private readonly GameObject root;
        private readonly Canvas canvas;
        private readonly RectTransform dot;
        private readonly Texture2D texture;

        internal bool IsAlive => root != null;

        internal static DesktopPointerVisuals Create()
        {
            try { return new DesktopPointerVisuals(); }
            catch (Exception error)
            {
                Logger.Default.Debug($"FPFC reticle unavailable: {error.Message}");
                return null;
            }
        }

        private DesktopPointerVisuals()
        {
            root = new GameObject("IPA FPFC Reticle", typeof(RectTransform), typeof(Canvas));
            try
            {
                Object.DontDestroyOnLoad(root);
                root.layer = 5;
                canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = short.MaxValue;
                canvas.pixelPerfect = true;
                var imageObject = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                imageObject.layer = 5;
                imageObject.transform.SetParent(root.transform, false);
                dot = imageObject.GetComponent<RectTransform>();
                dot.anchorMin = dot.anchorMax = dot.pivot = new Vector2(0.5f, 0.5f);
                dot.sizeDelta = new Vector2(6f, 6f);
                dot.anchoredPosition = Vector2.zero;
                texture = CreateDotTexture();
                var image = imageObject.GetComponent<RawImage>();
                image.texture = texture;
                image.raycastTarget = false;
                image.maskable = false;
                Hide();
            }
            catch { Dispose(); throw; }
        }

        private static Texture2D CreateDotTexture()
        {
            const int size = 32;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float distance = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude;
                byte shade = (byte)(255f * Mathf.Clamp01(size * 0.3f - distance));
                byte alpha = (byte)(255f * Mathf.Clamp01(size * 0.5f - distance));
                pixels[y * size + x] = new Color32(shade, shade, shade, alpha);
            }
            var result = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "IPA FPFC Dot",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            try
            {
                result.SetPixels32(pixels);
                result.Apply(false, true);
                return result;
            }
            catch { Object.Destroy(result); throw; }
        }

        internal void Draw(Camera camera)
        {
            if (root == null || camera == null) return;
            if (canvas.targetDisplay != camera.targetDisplay) canvas.targetDisplay = camera.targetDisplay;
            Vector2 center = camera.rect.center;
            if (dot.anchorMin != center || dot.anchorMax != center) dot.anchorMin = dot.anchorMax = center;
            if (!root.activeSelf) root.SetActive(true);
        }

        internal void Hide() { if (root != null && root.activeSelf) root.SetActive(false); }

        public void Dispose()
        {
            if (root != null) Object.Destroy(root);
            if (texture != null) Object.Destroy(texture);
        }
    }
}
