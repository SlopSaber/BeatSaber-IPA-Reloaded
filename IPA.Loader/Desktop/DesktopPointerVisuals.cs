using System;
using System.Reflection;
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
        private readonly RawImage image;
        private Texture2D texture;
        private Color coreColor;
        private Color glowColor;

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
                dot.sizeDelta = new Vector2(24f, 24f);
                dot.anchoredPosition = Vector2.zero;
                image = imageObject.GetComponent<RawImage>();
                image.raycastTarget = false;
                image.maskable = false;
                SetColors(new Color(0f, 0.5f, 1f), new Color(0.22f, 0.55f, 1f));
                Hide();
            }
            catch { Dispose(); throw; }
        }

        internal void UseNativeCursorStyle(Behaviour source)
        {
            try
            {
                var prefab = source.GetType().GetField("_cursorPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source) as Component;
                var renderer = prefab != null ? prefab.GetComponentInChildren<Renderer>(true) : null;
                var material = renderer != null ? renderer.sharedMaterial : null;
                if (material == null) return;
                Color color = material.HasProperty("_Color") ? material.GetColor("_Color")
                    : material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : new Color(0f, 0.5f, 1f);
                Color emission = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : color;
                if (emission.maxColorComponent <= 0f) emission = color;
                SetColors(color, emission);
            }
            catch (Exception error) { Logger.Default.Debug($"Native FPFC cursor style unavailable: {error.Message}"); }
        }

        private static Color NormalizeColor(Color color)
        {
            float scale = Mathf.Max(1f, color.maxColorComponent);
            return new Color(Mathf.Clamp01(color.r / scale), Mathf.Clamp01(color.g / scale), Mathf.Clamp01(color.b / scale), 1f);
        }

        private void SetColors(Color color, Color emission)
        {
            Color core = Color.Lerp(NormalizeColor(color), Color.white, 0.85f);
            Color glow = NormalizeColor(emission);
            if (texture != null && core == coreColor && glow == glowColor) return;
            Texture2D replacement = CreateDotTexture(core, glow);
            try { image.texture = replacement; }
            catch { Object.Destroy(replacement); throw; }
            Texture2D previous = texture;
            texture = replacement;
            coreColor = core;
            glowColor = glow;
            if (previous != null) Object.Destroy(previous);
        }

        private static Texture2D CreateDotTexture(Color core, Color glow)
        {
            const int size = 96;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float radius = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude / (size * 0.5f);
                float center = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.21f, 0.27f, radius));
                float halo = 0.55f * Mathf.Exp(-5.5f * radius * radius) * Mathf.Clamp01((1f - radius) * 6f);
                Color pixel = Color.Lerp(glow, core, center);
                pixel.a = center + halo * (1f - center);
                pixels[y * size + x] = pixel;
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
