using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS1591 // Public entry points are called by rewritten game assemblies.
namespace IPA.Data
{
    public static class NativeFileIO
    {
        public static string ReadAllText(string path) => ReadAllText(path, Encoding.UTF8);
        public static string ReadAllText(string path, Encoding encoding)
        {
            string raw = File.ReadAllText(path, encoding);
            return SaveDataStore.PrepareRead(path, raw);
        }
        public static void WriteAllText(string path, string contents) { File.WriteAllText(path, contents); SaveDataStore.RecordWrite(path); }
        public static void WriteAllText(string path, string contents, Encoding encoding) { File.WriteAllText(path, contents, encoding); SaveDataStore.RecordWrite(path); }
        public static void WriteAllBytes(string path, byte[] bytes) { File.WriteAllBytes(path, bytes); SaveDataStore.RecordWrite(path); }
        public static void Replace(string source, string destination, string backup) { File.Replace(source, destination, backup); SaveDataStore.RecordWrite(destination); }
        public static void Replace(string source, string destination, string backup, bool ignoreMetadataErrors) { File.Replace(source, destination, backup, ignoreMetadataErrors); SaveDataStore.RecordWrite(destination); }
        public static void Move(string source, string destination) { File.Move(source, destination); SaveDataStore.RecordWrite(destination); }
        public static void Copy(string source, string destination) { File.Copy(source, destination); SaveDataStore.RecordWrite(destination); }
        public static void Copy(string source, string destination, bool overwrite) { File.Copy(source, destination, overwrite); SaveDataStore.RecordWrite(destination); }
        public static Task<string> ReadAllTextAsync(string path) => Task.Run(() => ReadAllText(path));
        public static Task<string> ReadAllTextAsync(string path, Encoding encoding) => Task.Run(() => ReadAllText(path, encoding));
        public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) => Task.Run(() => ReadAllText(path), cancellationToken);
        public static Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken) => Task.Run(() => ReadAllText(path, encoding), cancellationToken);
        public static Task WriteAllTextAsync(string path, string contents) => Task.Run(() => WriteAllText(path, contents));
        public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) => Task.Run(() => WriteAllText(path, contents), cancellationToken);
        public static Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken) => Task.Run(() => WriteAllText(path, contents, encoding), cancellationToken);
    }
}
