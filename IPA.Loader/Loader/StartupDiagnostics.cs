using HarmonyLib;
using IPA.Logging;
using System;
using System.Linq;
using System.Reflection;

namespace IPA.Loader
{
    internal static class StartupDiagnostics
    {
        private static bool installed;
        private static readonly Harmony Patches = new("BSIPA.StartupDiagnostics");

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var transitions = assembly.GetType("MenuTransitionsHelper", false);
                if (transitions != null)
                    foreach (var method in transitions.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(method => method.Name == "RestartGame" && !method.IsAbstract))
                        Patch(method, nameof(RestartRequested));
            }
        }

        private static void Patch(MethodInfo method, string prefix)
        {
            try { Patches.Patch(method, prefix: new HarmonyMethod(typeof(StartupDiagnostics), prefix)); }
            catch (Exception error) { Logger.Default.Debug($"Could not attach restart diagnostics to {method.DeclaringType?.FullName}.{method.Name}: {error.Message}"); }
        }

        private static void RestartRequested(MethodBase __originalMethod)
            => Logger.Default.Debug($"Game restart requested through {__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}. Caller stack:\n{Environment.StackTrace}");

    }
}
