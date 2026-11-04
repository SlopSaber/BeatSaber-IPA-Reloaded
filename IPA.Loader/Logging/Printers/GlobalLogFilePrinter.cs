using IPA.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ionic.Zlib;

namespace IPA.Logging.Printers
{
    /// <summary>
    /// A printer for all messages to a unified log location.
    /// </summary>
    /// <remarks>
    /// This is the readable log (<c>_latest.log</c>): repeated messages are compacted and BSIPA's routine
    /// messages are hidden. <see cref="GlobalFullLogFilePrinter"/> writes every message to <c>_latest.full.log</c>.
    /// Both share one session name, and old pairs are packed together into one <c>.tar.gz</c>.
    /// </remarks>
    public class GlobalLogFilePrinter : GZFilePrinter
    {
        private const string FullSuffix = ".full";

        internal static readonly string SessionStem = $"{Utils.CurrentTime():yyyy.MM.dd.HH.mm.ss}";

        /// <summary>
        /// Provides a filter for this specific printer.
        /// </summary>
        /// <value>the filter level for this printer</value>
        public override Logger.LogLevel Filter
        {
            get => FollowsConsoleFilter ? filter & StandardLogger.PrintFilter : filter;
            set => filter = value;
        }

        private Logger.LogLevel filter = Logger.LogLevel.All;

        /// <summary>
        /// Whether this log shows only the levels the console shows (Debug needs --debug or Debug.ShowDebug).
        /// </summary>
        protected virtual bool FollowsConsoleFilter => true;

        /// <summary>
        /// Prints an entry to the associated file.
        /// </summary>
        /// <param name="level">the <see cref="Logger.Level"/> of the message</param>
        /// <param name="time">the <see cref="DateTime"/> the message was recorded at</param>
        /// <param name="logName">the name of the log that sent the message</param>
        /// <param name="message">the message to print</param>
        public override void Print(Logger.Level level, DateTime time, string logName, string message)
        {
            foreach (var line in removeControlCodes.Replace(message, "").Split(new[] { "\n", Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                FileWriter.WriteLine(Logger.LogFormat, line, logName, time, level.ToString().ToUpperInvariant());
        }

        /// <summary>
        /// Gets the <see cref="FileInfo"/> for the target file.
        /// </summary>
        /// <returns>the target file to write to</returns>
        protected override FileInfo GetFileInfo() => new(Path.Combine(LogsDirectory().FullName, SessionStem + FileSuffix + ".log"));

        /// <summary>
        /// The part of the file name between the session stem and the extension.
        /// </summary>
        protected virtual string FileSuffix => "";

        private static DirectoryInfo LogsDirectory()
        {
            var logsDir = new DirectoryInfo(StandardLogger.LogDirectory);
            logsDir.Create();
            return logsDir;
        }

        /// <summary>
        /// Packs each previous session's readable and full logs into one archive.
        /// </summary>
        protected override void CompressOldLogs(FileInfo target)
        {
            var sessions = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in target.Directory.EnumerateFiles("*.log", SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(file.Name);
                if (stem.EndsWith(FullSuffix, StringComparison.OrdinalIgnoreCase))
                    stem = stem.Substring(0, stem.Length - FullSuffix.Length);
                if (stem.StartsWith("_latest", StringComparison.OrdinalIgnoreCase) || stem == SessionStem) continue;
                if (!sessions.TryGetValue(stem, out var files))
                    sessions[stem] = files = new List<FileInfo>();
                files.Add(file);
            }

            foreach (var session in sessions)
            {
                if (session.Value.Count == 1)
                    CompressOldLog(session.Value[0]);
                else
                    PackSession(new FileInfo(Path.Combine(target.DirectoryName!, session.Key + ".tar.gz")), session.Value);
            }
        }

        private static async void PackSession(FileInfo archive, List<FileInfo> files)
        {
            try
            {
                await Task.Run(() =>
                {
                    using (var output = archive.Create())
                    using (var gz = new GZipStream(output, CompressionMode.Compress, CompressionLevel.BestCompression, false))
                    {
                        foreach (var file in files.OrderBy(f => f.Name.Length))
                            WriteTarEntry(gz, file);
                        gz.Write(new byte[1024], 0, 1024);
                    }
                    foreach (var file in files)
                        file.Delete();
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Default.Error("Error packing old log files:");
                Logger.Default.Error(e);
            }
        }

        // Minimal POSIX ustar entry: a 512-byte header followed by the data padded to 512 bytes.
        private static void WriteTarEntry(Stream output, FileInfo file)
        {
            var data = File.ReadAllBytes(file.FullName);
            var header = new byte[512];
            void Field(int offset, int length, string value)
            {
                var bytes = Encoding.ASCII.GetBytes(value);
                Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length));
            }
            string Octal(long value, int length) => Convert.ToString(value, 8).PadLeft(length - 1, '0') + "\0";

            Field(0, 100, file.Name);
            Field(100, 8, Octal(420, 8)); // 0644
            Field(108, 8, Octal(0, 8));
            Field(116, 8, Octal(0, 8));
            Field(124, 12, Octal(data.Length, 12));
            Field(136, 12, Octal((long)(file.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds, 12));
            Field(148, 8, "        ");
            header[156] = (byte)'0';
            Field(257, 6, "ustar\0");
            Field(263, 2, "00");
            int checksum = header.Sum(b => b);
            Field(148, 8, Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ");

            output.Write(header, 0, header.Length);
            output.Write(data, 0, data.Length);
            int padding = (512 - data.Length % 512) % 512;
            if (padding > 0) output.Write(new byte[padding], 0, padding);
        }
    }

    /// <summary>
    /// Writes every message, uncompacted and including BSIPA's routine messages, to <c>_latest.full.log</c>.
    /// </summary>
    public class GlobalFullLogFilePrinter : GlobalLogFilePrinter
    {
        /// <inheritdoc />
        protected override string FileSuffix => ".full";

        /// <inheritdoc />
        protected override bool FollowsConsoleFilter => false;

        /// <inheritdoc />
        protected override string GetLatestName(FileInfo target) => "_latest.full.log";

        /// <summary>
        /// The readable-log printer packs both files of a session, so this one does nothing.
        /// </summary>
        protected override void CompressOldLogs(FileInfo target) { }
    }
}
