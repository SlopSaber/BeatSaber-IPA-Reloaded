using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using IPA.Loader;
using Newtonsoft.Json.Linq;
using ModernFeature = IPA.Loader.Features.Feature;

namespace IPA.Compat4.Features
{
    /// <summary>Retains the string-argument feature API from early BSIPA 4.</summary>
    public abstract class Feature : ModernFeature
    {
        private static readonly ConditionalWeakTable<ModernFeature, Feature> Views = new();
        /// <summary>Initializes a feature using legacy positional arguments.</summary>
        public abstract bool Initialize(PluginMetadata metadata, string[] parameters);
        /// <summary>Evaluates a conditional feature.</summary>
        public virtual bool Evaluate() => true;
        /// <summary>Determines whether the plugin may be loaded.</summary>
        public virtual bool BeforeLoad(PluginMetadata metadata) => true;
        /// <summary>Determines whether the plugin may be initialized.</summary>
        public new virtual bool BeforeInit(PluginMetadata metadata) => true;
        /// <summary>Loads the plugin assembly without creating its instance.</summary>
        protected void RequireLoaded(PluginMetadata metadata) => IPA.Loader.PluginLoader.Load(metadata);
        /// <summary>Determines whether the feature appears in legacy metadata.</summary>
        protected internal virtual bool StoreOnPlugin => true;
        /// <inheritdoc />
        protected override bool Initialize(PluginMetadata meta, JObject featureData)
            => Initialize(meta, featureData["arguments"]?.ToObject<string[]>() ?? Array.Empty<string>());

        internal static IReadOnlyList<Feature> View(IEnumerable<ModernFeature> features) => features
            .Where(feature => feature is not Feature legacy || legacy.StoreOnPlugin)
            .Select(feature => feature as Feature ?? Views.GetValue(feature, source => new Adapter(source))).ToArray();

        private sealed class Adapter : Feature
        {
            private readonly ModernFeature source;
            internal Adapter(ModernFeature source) => this.source = source;
            public override string InvalidMessage { get => source.InvalidMessage; protected set => base.InvalidMessage = value; }
            public override bool Initialize(PluginMetadata metadata, string[] parameters) => true;
            public override bool BeforeInit(PluginMetadata metadata) { source.BeforeInit(metadata); return true; }
            public override void AfterInit(PluginMetadata plugin) => source.AfterInit(plugin);
            public override void AfterInit(PluginMetadata plugin, object pluginInstance) => source.AfterInit(plugin, pluginInstance);
            public override void AfterDisable(PluginMetadata plugin) => source.AfterDisable(plugin);
        }
    }
}
