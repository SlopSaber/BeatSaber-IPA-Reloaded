using System;
using System.Reflection;
using HarmonyLib;

namespace IPA.Loader
{
    /// <summary>Method lookup used by prepared legacy SiraUtil generators.</summary>
    public static class LegacyAffinityMethods
    {
        /// <summary>Preserves the parameterless fallback when legacy Affinity omits an overload signature.</summary>
        public static MethodInfo DeclaredMethod(Type type, string name, Type[] parameters, Type[] generics)
        {
            try { return AccessTools.DeclaredMethod(type, name, parameters, generics); }
            catch (AmbiguousMatchException) when (parameters == null)
            {
                // Older generators discard an explicit empty Type[] before invoking Harmony.
                var method = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes, generics);
                if (method == null) throw;
                return method;
            }
        }
    }
}
