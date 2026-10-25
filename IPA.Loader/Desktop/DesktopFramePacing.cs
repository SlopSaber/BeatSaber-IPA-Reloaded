using System;
using UnityEngine;
using Logger = IPA.Logging.Logger;

namespace IPA.Desktop
{
    [DefaultExecutionOrder(32000)]
    internal sealed class DesktopFramePacing : MonoBehaviour
    {
        private int originalVSyncCount;
        private int originalTargetFrameRate;
        private int desiredVSyncCount;
        private bool hasSavedSettings;

        internal static void Install(GameObject host)
        {
            if (!DesktopMode.Requested || host.GetComponent<DesktopFramePacing>() != null) return;
            try { host.AddComponent<DesktopFramePacing>(); }
            catch (Exception error) { Logger.Default.Error($"Could not initialize FPFC frame pacing: {error}"); }
        }

        private void OnEnable()
        {
            originalVSyncCount = QualitySettings.vSyncCount;
            originalTargetFrameRate = Application.targetFrameRate;
            desiredVSyncCount = DesktopMode.VSyncEnabled ? 1 : 0;
            hasSavedSettings = true;
            Apply();
            Logger.Default.Info(desiredVSyncCount == 1
                ? "FPFC frame pacing: monitor VSync enabled. Use --vsync-off for uncapped rendering."
                : "FPFC frame pacing: VSync disabled and software frame limit removed (--vsync-off).");
        }

        private void LateUpdate() { Apply(); }

        private void Apply()
        {
            if (!hasSavedSettings) return;
            if (QualitySettings.vSyncCount != desiredVSyncCount) QualitySettings.vSyncCount = desiredVSyncCount;
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
        }

        private void OnDisable()
        {
            if (!hasSavedSettings) return;
            hasSavedSettings = false;
            if (QualitySettings.vSyncCount == desiredVSyncCount) QualitySettings.vSyncCount = originalVSyncCount;
            if (Application.targetFrameRate == -1) Application.targetFrameRate = originalTargetFrameRate;
        }
    }
}
