using System;

namespace Doorstop
{
    /// <summary>Provides the managed entry point required by Unity Doorstop 4.</summary>
    public static class Entrypoint
    {
        /// <summary>Initializes BSIPA before Unity loads its game assemblies.</summary>
        public static void Start() => IPA.Injector.Injector.Main(Array.Empty<string>());
    }
}
