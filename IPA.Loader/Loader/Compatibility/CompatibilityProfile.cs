using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Hive.Versioning;
using Mono.Cecil;
using Newtonsoft.Json;
using Version = Hive.Versioning.Version;

namespace IPA.Loader.Compatibility
{
    internal sealed class CompatibilityProfile
    {
        private static readonly Version JsonFeaturesVersion = new("4.1.0");
        private static readonly Version OptionalGameVersion = new("4.2.1");

        [JsonConstructor]
        public CompatibilityProfile(string version) => Version = new Version(version);

        internal Version Version { get; }
        internal bool UsesLegacyFeatures => Version.CompareTo(JsonFeaturesVersion) < 0;
        internal bool AllowsNullId => Version.Major < 5;
        internal bool RequiresGameVersion => Version.CompareTo(OptionalGameVersion) < 0;
    }

    internal static class CompatibilityProfiles
    {
        private sealed class GameRelease
        {
            public string GameVersion = "";
            public string IpaVersion = "";
        }

        private sealed class Catalog
        {
            public CompatibilityProfile[] Releases = Array.Empty<CompatibilityProfile>();
            public GameRelease[] GameReleases = Array.Empty<GameRelease>();
        }

        private static readonly Catalog Data = ReadCatalog();
        internal static readonly CompatibilityProfile Current = new(new System.Version(IPA.Config.SelfConfig.IPAVersion).ToString(3));
        internal static IEnumerable<CompatibilityProfile> Releases => new[] { Current }.Concat(Data.Releases);

        // Do not allow eager catalog initialization while bootstrap libraries are being loaded.
        static CompatibilityProfiles() { }

        private static Catalog ReadCatalog()
        {
            using var stream = typeof(CompatibilityProfiles).Assembly.GetManifestResourceStream(
                "IPA.Loader.Compatibility.ReleaseCatalog.json") ?? throw new InvalidOperationException("Missing compatibility release catalog.");
            using var reader = new StreamReader(stream);
            return JsonConvert.DeserializeObject<Catalog>(reader.ReadToEnd())
                ?? throw new InvalidOperationException("Invalid compatibility release catalog.");
        }

        internal static Version GameBaseline(string game)
        {
            static System.Version ParseGame(string value)
            {
                if (value == null) return null;
                var match = Regex.Match(value, @"^\d+\.\d+\.\d+");
                return match.Success && System.Version.TryParse(match.Value, out var parsed) ? parsed : null;
            }
            var target = ParseGame(game);
            if (target == null) return null;
            var release = Data.GameReleases.Where(entry => ParseGame(entry.GameVersion) <= target)
                .OrderByDescending(entry => ParseGame(entry.GameVersion)).FirstOrDefault();
            return release == null ? null : new Version(release.IpaVersion);
        }

        // These signatures identify API boundaries even when a manifest's lower bound is stale.
        internal static Version MinimumBinaryVersion(ModuleDefinition module, TypeDefinition entryType = null)
        {
            if (module == null) return null;
            if (entryType != null && entryType.Methods.Any(method => method.ReturnType.FullName == "System.Threading.Tasks.Task"
                && method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "IPA.OnEnableAttribute"
                    || attribute.AttributeType.FullName == "IPA.OnDisableAttribute"))) return new Version("4.3.3");
            var methods = module.GetMemberReferences().OfType<MethodReference>().Where(method => IsLoaderType(method.DeclaringType)).ToArray();
            if (methods.Any(method => method.DeclaringType.FullName == "IPA.Utilities.Async.UnityMainThreadTaskScheduler"
                && method.Name == "get_Default" && method.ReturnType.FullName == "IPA.Utilities.Async.UnityMainThreadTaskScheduler"))
                return new Version("4.3.0");
            if (methods.Any(method => method.ReturnType.FullName == "Hive.Versioning.Version"
                || method.ReturnType.FullName == "Hive.Versioning.VersionRange")) return new Version("4.2.0");
            if (HasJsonFeatureContract(module)) return new Version("4.1.0");
            return null;
        }

        internal static bool IsLoaderType(TypeReference type)
        {
            while (type.DeclaringType != null) type = type.DeclaringType;
            return type.Scope is AssemblyNameReference assembly && assembly.Name == "IPA.Loader";
        }

        internal static bool HasJsonFeatureContract(ModuleDefinition module) => Types(module.Types).Any(type =>
            type.BaseType?.FullName == "IPA.Loader.Features.Feature" && IsLoaderType(type.BaseType)
            && type.Methods.Any(method => method.Name == "Initialize" && method.Parameters.Count == 2
                && method.Parameters[1].ParameterType.FullName == "Newtonsoft.Json.Linq.JObject"))
            || module.GetMemberReferences().OfType<MethodReference>().Any(method => IsLoaderType(method.DeclaringType)
                && method.DeclaringType.FullName == "IPA.Loader.Features.Feature" && method.Name == "BeforeInit"
                && method.ReturnType.FullName == "System.Void");

        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nested in Types(type.NestedTypes)) yield return nested;
            }
        }
    }
}
