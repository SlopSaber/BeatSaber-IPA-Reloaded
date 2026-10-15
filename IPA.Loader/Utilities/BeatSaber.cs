namespace IPA.Utilities
{
    /// <summary>Preserves the legacy game-path and main-thread API.</summary>
    public static class BeatSaber
    {
        /// <summary>Gets the selected game's version.</summary>
        public static AlmostVersion GameVersion => UnityGame.GameVersion;
        /// <summary>Gets the selected installation's absolute path.</summary>
        public static string InstallPath => UnityGame.InstallPath;
        /// <summary>Gets the selected installation's library directory.</summary>
        public static string LibraryPath => UnityGame.LibraryPath;
        /// <summary>Gets the selected installation's native-library directory.</summary>
        public static string NativeLibraryPath => UnityGame.NativeLibraryPath;
        /// <summary>Gets the selected installation's plugin directory.</summary>
        public static string PluginsPath => UnityGame.PluginsPath;
        /// <summary>Gets the selected installation's configuration directory.</summary>
        public static string UserDataPath => UnityGame.UserDataPath;
        /// <summary>Gets whether the current thread is the Unity main thread.</summary>
        public static bool OnMainThread => UnityGame.OnMainThread;
        /// <summary>Resumes execution on the Unity main thread.</summary>
        public static SwitchToUnityMainThreadAwaitable SwitchToMainThreadAsync() => UnityGame.SwitchToMainThreadAsync();
    }
}
