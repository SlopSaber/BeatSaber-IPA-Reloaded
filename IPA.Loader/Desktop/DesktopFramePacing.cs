using System;
using System.Diagnostics;
using UnityEngine;
using Logger = IPA.Logging.Logger;

namespace IPA.Desktop
{
    [DefaultExecutionOrder(32000)]
    internal sealed class DesktopFramePacing : MonoBehaviour
    {
        private int originalVSyncCount;
        private int originalTargetFrameRate;
        private int lastVSyncCount;
        private int lastTargetFrameRate;
        private bool hasSavedSettings;
        private bool vsyncEnabled;
        private bool brokenVSync;
        private bool softwareFallback;
        private readonly DesktopMonitor monitor = new();
        private int monitorRefreshRate;
        private string monitorDevice;
        private double nextMonitorCheck;
        private double sampleStart;
        private int sampleFrames;

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
            vsyncEnabled = DesktopMode.VSyncEnabled;
            brokenVSync = false;
            softwareFallback = false;
            sampleStart = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            nextMonitorCheck = sampleStart;
            sampleFrames = 0;
            hasSavedSettings = true;
            if (vsyncEnabled) Application.logMessageReceived += OnLogMessage;
            Apply();
            Logger.Default.Info(vsyncEnabled
                ? "FPFC frame pacing: monitor VSync requested; monitoring effective pacing. Use --vsync-off for uncapped rendering."
                : "FPFC frame pacing: VSync disabled and software frame limit removed (--vsync-off).");
        }

        private void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (message != null && message.IndexOf("vsync is broken", StringComparison.OrdinalIgnoreCase) >= 0) brokenVSync = true;
        }

        private void LateUpdate()
        {
            if (!hasSavedSettings) return;
            if (vsyncEnabled)
            {
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (now >= nextMonitorCheck)
                {
                    nextMonitorCheck = now + 0.5;
                    if (monitor.TryGetRefreshRate(out int rate, out string device))
                    {
                        if (rate != monitorRefreshRate || device != monitorDevice)
                        {
                            Logger.Default.Info($"FPFC display: {device}, {rate} Hz{(softwareFallback ? "; updating software FPS limit" : "")}.");
                            sampleStart = now;
                            sampleFrames = 0;
                        }
                        monitorRefreshRate = rate;
                        monitorDevice = device;
                    }
                }
                if (!softwareFallback && monitorRefreshRate > 1)
                {
                    sampleFrames++;
                    double elapsed = now - sampleStart;
                    if (elapsed >= 1)
                    {
                        if (sampleFrames / elapsed > monitorRefreshRate * 1.05 + 1) brokenVSync = true;
                        sampleStart = now;
                        sampleFrames = 0;
                    }
                    if (brokenVSync)
                    {
                        softwareFallback = true;
                        Logger.Default.Warn($"FPFC VSync is ineffective; using {monitorRefreshRate} FPS software pacing on {monitorDevice}. --vsync-off also disables this fallback.");
                    }
                }
            }
            Apply();
        }

        private void Apply()
        {
            if (!hasSavedSettings) return;
            lastVSyncCount = vsyncEnabled && !softwareFallback ? 1 : 0;
            lastTargetFrameRate = vsyncEnabled && softwareFallback ? monitorRefreshRate : -1;
            if (QualitySettings.vSyncCount != lastVSyncCount) QualitySettings.vSyncCount = lastVSyncCount;
            if (Application.targetFrameRate != lastTargetFrameRate) Application.targetFrameRate = lastTargetFrameRate;
        }

        private void OnDisable()
        {
            if (!hasSavedSettings) return;
            Application.logMessageReceived -= OnLogMessage;
            hasSavedSettings = false;
            if (QualitySettings.vSyncCount == lastVSyncCount) QualitySettings.vSyncCount = originalVSyncCount;
            if (Application.targetFrameRate == lastTargetFrameRate) Application.targetFrameRate = originalTargetFrameRate;
        }
    }
}
