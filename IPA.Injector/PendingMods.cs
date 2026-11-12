using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using IPA.Injector.Backups;
using IPA.Utilities;
using Logger = IPA.Logging.Logger;

namespace IPA.Injector
{
    internal static class PendingMods
    {
        private sealed class InstallSummary
        {
            internal int Installed, Queued, InstallErrors, StagingErrors, EarlierClaims, ScanErrors, LinkedDirectories;
            internal bool Incomplete;

            internal void Write()
            {
                if (Installed == 0 && Queued == 0 && !Incomplete) return;
                try
                {
                    string coverage = Incomplete ? "Queued count is incomplete; unscanned files may remain."
                        : "Counts cover files observed during this pass.";
                    Logger.Essential(Logger.Injector, $"Pending mods: {Installed} installed; {Queued} observed files still queued "
                        + $"(install/retry errors: {InstallErrors}, staging errors: {StagingErrors}, earlier claims: {EarlierClaims}). "
                        + $"Scan failures: {ScanErrors}; linked directories skipped: {LinkedDirectories}. {coverage} "
                        + "Remaining/new files retry next launch; replacements are backed up under IPA/Backups/Pending.");
                }
                catch { }
            }
        }

        internal static void Apply()
        {
            string pending = Path.GetFullPath(Path.Combine(UnityGame.InstallPath, "IPA", "Pending"));
            string mods = Path.GetFullPath(UnityGame.ModsPath);
            string claimed = Path.Combine(pending, ".installing");
            var summary = new InstallSummary();
            bool completed = false;
            try
            {
                if (Linked(pending, pending) || Linked(mods, mods) || Linked(claimed, pending))
                {
                    summary.Incomplete = true;
                    ++summary.LinkedDirectories;
                    Logger.Injector.Warn("Pending mod installation skipped: a queue or destination directory is linked.");
                    return;
                }
                Directory.CreateDirectory(pending);
                // A second instance leaves this launch's queue to its current owner.
                using var lease = new FileStream(Path.Combine(UnityGame.InstallPath, "IPA", "Pending.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var backup = new BackupUnit(Path.Combine(UnityGame.InstallPath, "IPA", "Backups", "Pending"),
                    Utils.CurrentTime().ToString("yyyy-MM-dd_HH-mm-ss") + "-" + Guid.NewGuid().ToString("N"));
                // Finish interrupted installs first. New copies at the public queue
                // path stay separate from the file this launch is installing.
                foreach (string file in Files(claimed, summary: summary))
                {
                    if (Install(file, claimed, mods, backup)) ++summary.Installed;
                    else { ++summary.Queued; ++summary.InstallErrors; }
                }
                foreach (string file in Files(pending, claimed, summary))
                {
                    string relative = Utils.GetRelativePath(file, pending);
                    string claim = Path.GetFullPath(Path.Combine(claimed, relative));
                    try
                    {
                        if (Linked(file, pending)) throw new IOException("Linked staging file.");
                        if (!Inside(claim, claimed) || Linked(claim, pending)) throw new IOException("Linked or invalid staging path.");
                        if (File.Exists(claim))
                        {
                            ++summary.Queued;
                            ++summary.EarlierClaims;
                            continue; // Its previous attempt still needs retrying.
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(claim));
                        File.Move(file, claim);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        ++summary.Queued;
                        ++summary.StagingErrors;
                        Logger.Injector.Warn($"Pending mod file '{relative}' remains queued: {error.Message}");
                        continue;
                    }
                    if (Install(claim, claimed, mods, backup)) ++summary.Installed;
                    else { ++summary.Queued; ++summary.InstallErrors; }
                }
                RemoveEmptyDirectories(pending);
                completed = true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                summary.Incomplete = true;
                Logger.Injector.Warn($"Pending mod installation could not finish; remaining files will retry next launch: {error.Message}");
            }
            finally
            {
                summary.Incomplete |= !completed;
                summary.Write();
            }
        }

        private static bool Install(string source, string claimed, string mods, BackupUnit backup)
        {
            string relative = Utils.GetRelativePath(source, claimed);
            string target = Path.GetFullPath(Path.Combine(mods, relative));
            string temporary = target + ".bsipa-pending-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (!Inside(source, claimed) || !Inside(target, mods) || Linked(source, claimed) || Linked(target, mods))
                    throw new IOException("Linked or invalid pending mod path.");
                // Exclusive access defers files whose producer is still copying.
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    using (var hash = SHA256.Create())
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                        input.Position = 0;
                        output.Position = 0;
                        if (!hash.ComputeHash(input).SequenceEqual(hash.ComputeHash(output))) throw new IOException("Pending mod copy failed content verification.");
                    }
                    if (File.Exists(target))
                    {
                        backup.Add(target);
                        File.Replace(temporary, target, null);
                    }
                    else File.Move(temporary, target);
                }
                File.Delete(source);
                Logger.Essential(Logger.Injector, $"Installed pending mod file '{relative}' into Mods.");
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Logger.Injector.Warn($"Pending mod file '{relative}' remains queued: {error.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Logger.Injector.Warn($"Could not remove pending mod temporary file '{temporary}': {error.Message}"); }
            }
        }

        private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static bool Linked(string path, string root)
        {
            for (string current = path; current != null && Inside(current, root); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }

        private static IEnumerable<string> Directories(string root, string except = null, InstallSummary summary = null)
        {
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                string directory = directories.Pop();
                string[] children;
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    if (Linked(directory, root))
                    {
                        if (summary != null) { ++summary.LinkedDirectories; summary.Incomplete = true; }
                        continue;
                    }
                    children = Directory.GetDirectories(directory);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    if (summary != null) { ++summary.ScanErrors; summary.Incomplete = true; }
                    Logger.Injector.Warn($"Cannot scan pending mod directory '{directory}': {error.Message}");
                    continue;
                }
                yield return directory;
                foreach (string child in children.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                    if (!child.Equals(except, StringComparison.OrdinalIgnoreCase)) directories.Push(child);
            }
        }

        private static IEnumerable<string> Files(string root, string except = null, InstallSummary summary = null)
        {
            foreach (string directory in Directories(root, except, summary))
            {
                string[] files;
                try { files = Directory.GetFiles(directory); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    if (summary != null) { ++summary.ScanErrors; summary.Incomplete = true; }
                    Logger.Injector.Warn($"Cannot scan pending mod files in '{directory}': {error.Message}");
                    continue;
                }
                foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) yield return file;
            }
        }

        private static void RemoveEmptyDirectories(string root)
        {
            foreach (string directory in Directories(root).OrderByDescending(path => path.Length))
            {
                if (directory.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Logger.Injector.Debug($"Pending directory cleanup deferred for '{directory}': {error.Message}"); }
            }
        }
    }
}
