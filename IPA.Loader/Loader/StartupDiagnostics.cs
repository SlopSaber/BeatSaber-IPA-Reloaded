using HarmonyLib;
using IPA.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace IPA.Loader
{
    internal static class StartupDiagnostics
    {
        private static bool installed;
        private static bool bundleHookInstalled;
        private static int unfinishedBundleReads;
        private static readonly Harmony Patches = new("BSIPA.StartupDiagnostics");

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var bundleRequest = assembly.GetType("UnityEngine.AssetBundleCreateRequest", false);
                var bundleGetter = bundleRequest?.GetProperty("assetBundle", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod();
                if (!bundleHookInstalled && bundleGetter?.GetMethodBody() != null)
                {
                    try
                    {
                        Patches.Patch(bundleGetter,
                            prefix: new HarmonyMethod(typeof(StartupDiagnostics), nameof(BundleReadBeginning)),
                            postfix: new HarmonyMethod(typeof(StartupDiagnostics), nameof(BundleReadEnded)));
                        bundleHookInstalled = true;
                        Logger.Default.Debug("Asset bundle completion diagnostics installed.");
                    }
                    catch (Exception error) { Logger.Default.Debug($"Could not attach bundle completion diagnostics: {error.Message}"); }
                }
                var transitions = assembly.GetType("MenuTransitionsHelper", false);
                if (transitions != null)
                    foreach (var method in transitions.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method => method.Name == "RestartGame" && !method.IsAbstract))
                        Patch(method, nameof(RestartRequested));
            }
            if (!bundleHookInstalled) Logger.Default.Debug("Asset bundle completion diagnostics unavailable: no managed request getter was loaded.");
        }

        private static void Patch(MethodInfo method, string prefix)
        {
            try { Patches.Patch(method, prefix: new HarmonyMethod(typeof(StartupDiagnostics), prefix)); }
            catch (Exception error) { Logger.Default.Debug($"Could not attach restart diagnostics to {method.DeclaringType?.FullName}.{method.Name}: {error.Message}"); }
        }

        private static void RestartRequested(MethodBase __originalMethod)
            => Logger.Default.Debug($"Game restart requested through {__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}. Caller stack:\n{Environment.StackTrace}");

        private static void BundleReadBeginning(UnityEngine.AsyncOperation __instance, out long __state)
        {
            __state = 0;
            if (__instance.isDone || Interlocked.Increment(ref unfinishedBundleReads) > 8) return;
            __state = Stopwatch.GetTimestamp();
            Logger.Default.Debug($"Waiting synchronously for an unfinished asset bundle. Caller stack:\n{Environment.StackTrace}");
        }

        private static void BundleReadEnded(long __state)
        {
            if (__state == 0) return;
            double milliseconds = (Stopwatch.GetTimestamp() - __state) * 1000d / Stopwatch.Frequency;
            Logger.Default.Debug($"Returned from synchronous asset bundle completion after {milliseconds:0.###} ms.");
        }

    }
}
