using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Logger = IPA.Logging.Logger;

namespace IPA.Data
{
    internal static class GameDataLocation
    {
        internal static string Resolve(string appData, string userData)
        {
            string destination = Path.GetFullPath(Path.Combine(appData, "Hyperbolic Magnetism"));
            string previous = Path.GetFullPath(Path.Combine(userData, "Hyperbolic Magnetism"));
            try { return Migrate(appData, previous, destination); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                if (Directory.Exists(destination)) return destination;
                if (!Directory.Exists(previous)) throw;
                Logger.Default.Warn($"Game AppData migration deferred; using previous saves at '{previous}' this launch: {error.Message}");
                return previous;
            }
        }

        private static string Migrate(string appData, string previous, string destination)
        {
            if (!Directory.Exists(previous)) return destination;
            if (Directory.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    Logger.Default.Warn($"Using existing game AppData at '{destination}'; previous saves at '{previous}' are retained for reconciliation.");
                    return destination;
                }
                Directory.Delete(destination); // An empty ordinary directory contains no save store.
            }

            Directory.CreateDirectory(appData); // AppData itself may be a directory link.
            if (OwnersInUse(previous))
            {
                Logger.Default.Warn("Game AppData migration deferred while a previous save session is active; retaining its current location this launch.");
                return previous;
            }
            if ((File.GetAttributes(previous) & FileAttributes.ReparsePoint) == 0)
            {
                try
                {
                    Directory.Move(previous, destination);
                    Logger.Default.Info("Moved game saves from UserData/Hyperbolic Magnetism into AppData/Hyperbolic Magnetism.");
                    return destination;
                }
                catch (IOException) { /* Cross-volume links need a verified copy instead. */ }
            }

            // Stage beside the final directory so publication is a same-volume
            // rename even when AppData points at a different disk.
            string staging = Path.GetFullPath(Path.Combine(appData, ".bsipa-migration-" + Guid.NewGuid().ToString("N")));
            try
            {
                CopyVerified(previous, staging);
                Directory.Move(staging, destination);
                string retained = previous + ".bsipa-backup-" + Guid.NewGuid().ToString("N");
                try { Directory.Move(previous, retained); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    retained = previous;
                    Logger.Default.Warn($"Could not rename the previous save snapshot: {error.Message}");
                }
                Logger.Default.Info($"Copied previous game saves into AppData/Hyperbolic Magnetism; original data remains at '{retained}' for rollback.");
                return destination;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // A concurrent migration may have published the destination.
                if (Directory.Exists(destination)) return destination;
                Logger.Default.Warn($"Game AppData migration deferred; using previous saves at '{previous}' this launch: {error.Message}");
                return previous;
            }
            finally
            {
                string parent = Path.GetDirectoryName(staging);
                if (string.Equals(parent, Path.GetFullPath(appData).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    { Logger.Default.Warn($"Could not remove migration staging directory '{staging}': {error.Message}"); }
                }
            }
        }

        private static bool OwnersInUse(string root)
        {
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                string owner = Path.Combine(directory, ".bsipa-owner");
                if (File.Exists(owner))
                {
                    try { using var stream = new FileStream(owner, FileMode.Open, FileAccess.Read, FileShare.Read); }
                    catch (IOException) { return true; }
                }
                foreach (string child in Directory.GetDirectories(directory))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) directories.Push(child);
            }
            return false;
        }

        private static void CopyVerified(string source, string destination)
        {
            var directories = new Stack<(string Source, string Destination)>();
            directories.Push((source, destination));
            while (directories.Count > 0)
            {
                var current = directories.Pop();
                Directory.CreateDirectory(current.Destination);
                foreach (string file in Directory.GetFiles(current.Source))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"A linked save file needs manual migration: {file}");
                    string target = Path.Combine(current.Destination, Path.GetFileName(file));
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    input.CopyTo(output);
                    output.Flush(true);
                    input.Position = 0;
                    output.Position = 0;
                    using var hash = SHA256.Create();
                    if (!hash.ComputeHash(input).SequenceEqual(hash.ComputeHash(output))) throw new IOException($"Save migration content verification failed: {file}");
                }
                foreach (string child in Directory.GetDirectories(current.Source))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"A linked save subdirectory needs manual migration: {child}");
                    directories.Push((child, Path.Combine(current.Destination, Path.GetFileName(child))));
                }
            }
        }
    }
}
