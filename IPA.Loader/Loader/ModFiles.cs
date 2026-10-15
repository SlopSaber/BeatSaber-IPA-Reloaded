#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using IPA.Logging;
using IPA.Utilities;
using Microsoft.Win32.SafeHandles;

namespace IPA.Loader
{
    internal static class ModFiles
    {
        internal static IEnumerable<string> Roots => new[] { UnityGame.ModsPath, UnityGame.PluginsPath };

        internal static IEnumerable<string> Directories(IEnumerable<string> roots, bool includeNative = false)
        {
            var pending = new Stack<string>(roots.Reverse());
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                string[] children;
                try
                {
                    if (!Directory.Exists(directory) || !visited.Add(DirectoryIdentity(directory))) continue;
                    children = Directory.GetDirectories(directory);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    LibLoader.Log(Logger.Level.Warning, $"Cannot scan mod directory '{directory}': {error.Message}");
                    continue;
                }
                yield return directory;
                foreach (string child in children.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(child);
                    if (name.Equals(".cache", StringComparison.OrdinalIgnoreCase) || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                        || (!includeNative && name.Equals("Native", StringComparison.OrdinalIgnoreCase))) continue;
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT && (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(child);
                }
            }
        }

        internal static IEnumerable<string> Files(IEnumerable<string> roots, string pattern, bool includeNative = false)
        {
            foreach (string directory in Directories(roots, includeNative))
            {
                string[] files;
                try { files = Directory.GetFiles(directory, pattern); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { LibLoader.Log(Logger.Level.Warning, $"Cannot scan mod files in '{directory}': {error.Message}"); continue; }
                foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) yield return file;
            }
        }

        internal static IEnumerable<string> PluginFiles => Files(Roots, "*.dll");
        internal static IEnumerable<string> Manifests => Files(Roots, "*.manifest").Concat(Files(Roots, "*.json"));

        internal static string ResolveDeclaredFile(PluginMetadata owner, string declared)
        {
            string relative = declared.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string original = Path.GetFullPath(Path.Combine(UnityGame.InstallPath, relative));
            if (File.Exists(original) || Path.IsPathRooted(relative)) return original;
            string directory = owner.File.DirectoryName ?? UnityGame.InstallPath;
            var candidates = new List<string> { Path.Combine(directory, relative) };
            var components = relative.Split(Path.DirectorySeparatorChar);
            bool library = components[0].Equals("Libs", StringComparison.OrdinalIgnoreCase)
                || (components.Length > 1 && components[0].Equals("IPA", StringComparison.OrdinalIgnoreCase) && components[1].Equals("Libs", StringComparison.OrdinalIgnoreCase));
            if (library)
            {
                string tail = string.Join(Path.DirectorySeparatorChar.ToString(), components.Skip(components[0].Equals("IPA", StringComparison.OrdinalIgnoreCase) ? 2 : 1));
                candidates.Add(Path.Combine(directory, tail));
                candidates.Add(Path.Combine(directory, "Libs", tail));
                candidates.Add(Path.Combine(UnityGame.InstallPath, "Libs", tail));
                candidates.Add(Path.Combine(UnityGame.InstallPath, "IPA", "Libs", tail));
            }
            else if (components[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase) || components[0].Equals("Mods", StringComparison.OrdinalIgnoreCase))
                candidates.Add(Path.Combine(directory, string.Join(Path.DirectorySeparatorChar.ToString(), components.Skip(1))));
            string? existing = candidates.FirstOrDefault(File.Exists);
            if (existing != null) return Path.GetFullPath(existing);
            if (library || components[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase) || components[0].Equals("Mods", StringComparison.OrdinalIgnoreCase))
            {
                var roots = Roots.Concat(new[] { Path.Combine(UnityGame.InstallPath, "Libs"), Path.Combine(UnityGame.InstallPath, "IPA", "Libs") });
                var matches = Files(roots, Path.GetFileName(relative), true).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                if (matches.Length == 1) return matches[0];
            }
            return original;
        }

        private static string DirectoryIdentity(string directory)
        {
            string fullPath = Path.GetFullPath(directory);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return fullPath;
            using var handle = OpenDirectory(fullPath, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) return fullPath;
            var buffer = new StringBuilder(512);
            uint length = GetDirectoryPath(handle, buffer, (uint)buffer.Capacity, 0);
            if (length >= buffer.Capacity)
            {
                buffer.Capacity = checked((int)length + 1);
                length = GetDirectoryPath(handle, buffer, (uint)buffer.Capacity, 0);
            }
            return length == 0 ? fullPath : buffer.ToString();
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetDirectoryPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    }
}
