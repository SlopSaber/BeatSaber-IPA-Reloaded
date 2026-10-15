using System;
using System.Reflection;
using IPA.Logging;

namespace IPA.Loader.Features
{
    internal sealed class LegacyPrintFeature : IPA.Compat4.Features.Feature
    {
        public override bool Initialize(PluginMetadata metadata, string[] parameters)
        {
            var level = FeatureName == "IPA.Legacy.Debug" ? Logger.Level.Debug : FeatureName == "IPA.Legacy.Warn" ? Logger.Level.Warning : Logger.Level.Info;
            Logger.Features.Log(level, $"{metadata.Name}: {string.Join(" ", parameters)}");
            return true;
        }
    }
    internal sealed class LegacyNoUpdateFeature : IPA.Compat4.Features.Feature
    {
        public override bool Initialize(PluginMetadata metadata, string[] parameters) => metadata.Id != null;
    }
    internal sealed class LegacyInitInjectorFeature : IPA.Compat4.Features.Feature
    {
        protected internal override bool StoreOnPlugin => false;
        public override bool Initialize(PluginMetadata metadata, string[] parameters)
        {
            if (parameters.Length != 2) { InvalidMessage = "Expected an injected type and Type:Method."; return false; }
            RequireLoaded(metadata);
            var targetType = Type.GetType(parameters[0], false);
            var methodParts = parameters[1].Split(':');
            if (targetType == null || methodParts.Length != 2) { InvalidMessage = "Invalid injector type or method."; return false; }
            var methodType = metadata.Assembly.GetType(methodParts[0], true);
            bool legacy3 = metadata.TargetLoaderVersion?.Major == 1;
            var metadataType = legacy3 ? typeof(IPA.Compat3.PluginLoader.PluginMetadata) : typeof(PluginMetadata);
            var method = methodType.GetMethod(methodParts[1], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(object), typeof(ParameterInfo), metadataType }, null);
            if (method == null) { InvalidMessage = "Injector method signature does not match the plugin API contract."; return false; }
            if (legacy3)
                IPA.Compat3.PluginInitInjector.AddInjector(targetType,
                    (IPA.Compat3.PluginInitInjector.InjectParameter)Delegate.CreateDelegate(typeof(IPA.Compat3.PluginInitInjector.InjectParameter), method));
            else
                PluginInitInjector.AddInjector(targetType,
                    (PluginInitInjector.InjectParameter)Delegate.CreateDelegate(typeof(PluginInitInjector.InjectParameter), method));
            return true;
        }
    }
}
