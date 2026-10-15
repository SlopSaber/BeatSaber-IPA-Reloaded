using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Metadata = IPA.Compat3.PluginLoader.PluginMetadata;
using Info = IPA.Compat3.PluginLoader.PluginInfo;
using CoreMetadata = IPA.Loader.PluginMetadata;
using ModernFeature = IPA.Loader.Features.Feature;

namespace IPA.Compat3.Features
{
    /// <summary>Retains the BSIPA 3 feature API and its metadata identities.</summary>
    public abstract class Feature : IPA.Compat4.Features.Feature
    {
        private static readonly ConditionalWeakTable<ModernFeature, Feature> Views = new();
        /// <summary>Initializes a feature using BSIPA 3 metadata.</summary>
        public abstract bool Initialize(Metadata metadata, string[] parameters);
        /// <summary>Determines whether the plugin may be loaded.</summary>
        public virtual bool BeforeLoad(Metadata metadata) => true;
        /// <summary>Determines whether the plugin may be initialized.</summary>
        public virtual bool BeforeInit(Info plugin) => true;
        /// <summary>Receives an initialized plugin.</summary>
        public virtual void AfterInit(Info plugin) { }
        /// <summary>Receives an initialized interface plugin.</summary>
        public virtual void AfterInit(Info plugin, IBeatSaberPlugin instance) => AfterInit(plugin);
        /// <summary>Loads the original plugin assembly.</summary>
        protected void RequireLoaded(Metadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (metadata.CoreMetadata != null)
            {
                IPA.Loader.PluginLoader.Load(metadata.CoreMetadata);
                metadata.Assembly = metadata.CoreMetadata.Assembly;
            }
        }
        /// <inheritdoc />
        public override bool Initialize(CoreMetadata metadata, string[] parameters) => Initialize(PluginManager.Convert(metadata), parameters);
        /// <inheritdoc />
        public override bool BeforeLoad(CoreMetadata metadata) => BeforeLoad(PluginManager.Convert(metadata));
        /// <inheritdoc />
        public override bool BeforeInit(CoreMetadata metadata) => BeforeInit(InfoFor(metadata));
        /// <inheritdoc />
        public override void AfterInit(CoreMetadata plugin) => AfterInit(InfoFor(plugin));
        /// <inheritdoc />
        public override void AfterInit(CoreMetadata plugin, object pluginInstance)
        {
            var info = InfoFor(plugin);
            AfterInit(info, info.Plugin ?? pluginInstance as IBeatSaberPlugin);
        }
        private static Info InfoFor(CoreMetadata metadata) => PluginManager.Legacy.FirstOrDefault(info => info.Metadata.CoreMetadata == metadata)
            ?? new Info { Metadata = PluginManager.Convert(metadata) };

        internal new static IReadOnlyList<Feature> View(IEnumerable<ModernFeature> features) => IPA.Compat4.Features.Feature.View(features)
            .Select(feature => feature as Feature ?? Views.GetValue(feature, source => new Adapter((IPA.Compat4.Features.Feature)source))).ToArray();
        private sealed class Adapter : Feature
        {
            private readonly IPA.Compat4.Features.Feature source;
            internal Adapter(IPA.Compat4.Features.Feature source) => this.source = source;
            public override string InvalidMessage { get => source.InvalidMessage; protected set => base.InvalidMessage = value; }
            public override bool Initialize(Metadata metadata, string[] parameters) => true;
            public override bool Evaluate() => source.Evaluate();
            public override bool BeforeLoad(Metadata metadata) => metadata.CoreMetadata == null || source.BeforeLoad(metadata.CoreMetadata);
            public override bool BeforeInit(Info plugin) => plugin.Metadata.CoreMetadata == null || source.BeforeInit(plugin.Metadata.CoreMetadata);
        }
    }
}
