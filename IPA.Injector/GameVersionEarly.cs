#nullable enable
using IPA.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace IPA.Injector
{
    internal static class GameVersionEarly
    {
        private const string GlobalGameManagersFileName = "globalgamemanagers";
        private const string AppStoreCategory = "public.app-category.games";

        internal static string ResolveDataPath(string installDir)
        {
            string expected = Path.Combine(installDir, Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]) + "_Data");
            if (Directory.Exists(expected)) return expected;
#if BeatSaber
            expected = Path.Combine(installDir, "Beat Saber_Data");
            if (Directory.Exists(expected)) return expected;
#endif
            var candidates = Directory.GetDirectories(installDir, "*_Data");
            if (candidates.Length == 1) return candidates[0];
            throw new DirectoryNotFoundException($"Could not unambiguously select the game's data directory in {installDir}.");
        }

        private static string GetGameVersion()
        {
            var filePath = Path.Combine(ResolveDataPath(UnityGame.InstallPath), GlobalGameManagersFileName);
            using var fileStream = File.OpenRead(filePath);
            using var binaryReader = new BinaryReader(fileStream, Encoding.UTF8);

            if (!TrySeekToKey(binaryReader, AppStoreCategory))
            {
                throw new KeyNotFoundException($"Could not find key '{AppStoreCategory}' in {GlobalGameManagersFileName}");
            }

            if (!TryFindVersion(binaryReader, out var gameVersion))
            {
                throw new InvalidDataException($"Could not find a valid game version string in {GlobalGameManagersFileName}");
            }

            return gameVersion;
        }

        private static bool TrySeekToKey(BinaryReader reader, string key)
        {
            var position = 0;
            var stream = reader.BaseStream;
            while (stream.Position < stream.Length && position < key.Length)
            {
                if (reader.ReadByte() == key[position])
                {
                    position++;
                }
                else
                {
                    position = 0;
                }
            }

            return stream.Position != stream.Length;
        }

        private static bool TryFindVersion(BinaryReader reader, [MaybeNullWhen(false)] out string version)
        {
            var stream = reader.BaseStream;
            while (stream.Position < stream.Length)
            {
                var current = (char)reader.ReadByte();

                if (!char.IsDigit(current))
                {
                    continue;
                }

                var bytesRead = 1;
                var dotCount = 0;

                while (stream.Position < stream.Length)
                {
                    current = (char)reader.ReadByte();
                    bytesRead++;

                    if (current == '.')
                    {
                        dotCount++;
                    }
                    else if (!char.IsDigit(current))
                    {
                        break;
                    }

                    // Ensures we get a valid SemVer.
                    if (dotCount == 2)
                    {
                        var resumePosition = stream.Position;
                        if (resumePosition < bytesRead + sizeof(int)) break;
                        stream.Seek(-bytesRead - sizeof(int), SeekOrigin.Current);
                        var lengthPrefix = reader.ReadInt32();
                        if (lengthPrefix > 0 && lengthPrefix <= 256 && lengthPrefix <= stream.Length - stream.Position)
                        {
                            version = Encoding.UTF8.GetString(reader.ReadBytes(lengthPrefix));
                            var parsed = new AlmostVersion(version.Split('_')[0]);
                            if (parsed.StorageMode == AlmostVersion.StoredAs.SemVer) return true;
                        }
                        // Resume after the original candidate rather than trusting its prefix.
                        stream.Position = resumePosition;
                        break;
                    }
                }
            }

            version = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Load()
        {
            UnityGame.SetEarlyGameVersion(new AlmostVersion(GetGameVersion()));
            UnityGame.CheckGameVersionBoundary();
        }
    }
}
