#nullable enable

using IPA.Config;
using IPA.Logging.Printers;
using IPA.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace IPA.Logging
{
    /// <summary>
    /// The default (and standard) <see cref="Logger"/> implementation.
    /// </summary>
    /// <remarks>
    /// <see cref="StandardLogger"/> uses a multi-threaded approach to logging. All actual I/O is done on another thread,
    /// where all messaged are guaranteed to be logged in the order they appeared. It is up to the printers to format them.
    ///
    /// This logger supports child loggers. Use <see cref="LoggerExtensions.GetChildLogger"/> to safely get a child.
    /// The modification of printers on a parent are reflected down the chain.
    /// </remarks>
    public class StandardLogger : Logger
    {
        private static readonly List<LogPrinter> defaultPrinters = new();
        // Receive every message before loader filtering and repeat compaction (_latest.full.log).
        private static readonly List<LogPrinter> fullPrinters = new();
        private static readonly ConcurrentDictionary<string, PluginLogFilePrinter> logFilePrinters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, PluginSubLogPrinter> subLogFilePrinters = new(StringComparer.OrdinalIgnoreCase);

        static StandardLogger()
        {
            ConsoleColorSupport();
        }

        private static bool addedConsolePrinters;
        private static bool finalizedDefaultPrinters;
        internal static void ConsoleColorSupport()
        {
            if (!addedConsolePrinters && !finalizedDefaultPrinters && WinConsole.IsInitialized )
            {
                defaultPrinters.AddRange(new []
                {
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.TraceOnly,
                        Color = ConsoleColor.DarkMagenta,
                    },
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.DebugOnly,
                        Color = ConsoleColor.Green,
                    },
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.InfoOnly,
                        Color = ConsoleColor.White,
                    },
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.NoticeOnly,
                        Color = ConsoleColor.Cyan
                    },
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.WarningOnly,
                        Color = ConsoleColor.Yellow,
                    },
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.ErrorOnly,
                        Color = ConsoleColor.Red,
                    },
                    new ColoredConsolePrinter()
                    {
                        Filter = LogLevel.CriticalOnly,
                        Color = ConsoleColor.Magenta,
                    }
                });

                addedConsolePrinters = true;
            }
        }

        /// <summary>
        /// The <see cref="TextWriter"/> for writing directly to the console window, or stdout if no window open.
        /// </summary>
        /// <value>a <see cref="TextWriter"/> for the current primary text output</value>
        public static TextWriter ConsoleWriter { get; internal set; } = Console.Out;

        /// <summary>
        /// Adds to the default printer pool that all printers inherit from. Printers added this way will be passed every message from every logger.
        /// </summary>
        /// <param name="printer">the printer to add</param>
        internal static void AddDefaultPrinter(LogPrinter printer)
        {
            defaultPrinters.Add(printer);
        }

        private readonly string logName;
        // Loggers owned by BSIPA itself; their routine messages are hidden unless requested.
        private readonly bool loaderOwned;
        private static bool showSourceClass;

        /// <summary>
        /// All levels defined by this filter will be sent to loggers. All others will be ignored.
        /// </summary>
        /// <value>the global filter level</value>
        public static LogLevel PrintFilter { get; internal set; } = LogLevel.All;
        private static bool showTrace;
        // Cached by Configure: reading the generated config here would re-enter its lock when the
        // config store itself logs while saving.
        private static volatile bool showLoaderMessages;
        private static volatile bool syncLogging;

        private readonly List<LogPrinter> printers = new();
        private readonly StandardLogger? parent;

        private readonly Dictionary<string, StandardLogger> children = new();

        private static bool addedFilePrinter;
        /// <summary>
        /// Configures internal debug settings based on the config passed in.
        /// </summary>
        internal static void Configure()
        {
            showSourceClass = SelfConfig.Debug_.ShowCallSource_;
            PrintFilter = SelfConfig.Debug_.ShowDebug_ ? LogLevel.All : LogLevel.InfoUp;
            showLoaderMessages = SelfConfig.Debug_.ShowLoaderMessages_;
            showTrace = SelfConfig.Debug_.ShowTrace_;
            syncLogging = SelfConfig.Debug_.SyncLogging_;
            if (SelfConfig.CommandLineValues.WriteLogs && !addedFilePrinter)
            {
                addedFilePrinter = true;
                AddDefaultPrinter(new GlobalLogFilePrinter());
                fullPrinters.Add(new GlobalFullLogFilePrinter());
            }
        }

        private StandardLogger(StandardLogger parent, string subName)
        {
            logName = $"{parent.logName}/{subName}";
            this.parent = parent;
            loaderOwned = parent.loaderOwned;
            printers = new List<LogPrinter>();
            if (SelfConfig.Debug_.CreateModLogs_ && !SelfConfig.Debug_.CondenseModLogs_)
                printers.Add(subLogFilePrinters.GetOrAdd(logName, new PluginSubLogPrinter(parent.logName, subName)));

            if (logThread == null || !logThread.IsAlive)
            {
                logThread = new Thread(LogThread);
                logThread.Start();
            }
        }

        internal StandardLogger(string name)
        {
            ConsoleColorSupport();
            if (!finalizedDefaultPrinters)
            {
                if (!addedConsolePrinters)
                    AddDefaultPrinter(new ColorlessConsolePrinter());

                finalizedDefaultPrinters = true;
            }

            logName = name;
            loaderOwned = name == "IPA" || name == "Harmony";
            if (SelfConfig.Debug_.CreateModLogs_)
                printers.Add(logFilePrinters.GetOrAdd(name, new PluginLogFilePrinter(name)));

            if (logThread == null || !logThread.IsAlive)
            {
                logThread = new Thread(LogThread);
                logThread.Start();
            }
        }

        /// <summary>
        /// Gets a child printer with the given name, either constructing a new one or using one that was already made.
        /// </summary>
        /// <param name="name"></param>
        /// <returns>a child <see cref="StandardLogger"/> with the given sub-name</returns>
        internal StandardLogger GetChild(string name)
        {
            if (!children.TryGetValue(name, out var child))
            {
                child = new StandardLogger(this, name);
                children.Add(name, child);
            }

            return child;
        }

        /// <summary>
        /// Adds a log printer to the logger.
        /// </summary>
        /// <param name="printer">the printer to add</param>
        public void AddPrinter(LogPrinter printer)
        {
            printers.Add(printer);
        }

        /// <summary>
        /// Logs a specific message at a given level.
        /// </summary>
        /// <param name="level">the message level</param>
        /// <param name="message">the message to log</param>
        public override void Log(Level level, string message) => Enqueue(level, message, essential: false);

        /// <summary>
        /// Logs a loader message that is always shown, such as the list of loaded plugins.
        /// </summary>
        internal void LogEssential(Level level, string message) => Enqueue(level, message, essential: true);

        private const byte ProblemLevels = (byte)(Level.Warning | Level.Error | Level.Critical);

        private void Enqueue(Level level, string message, bool essential)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            // FIXME: trace doesn't seem to ever actually appear
            if (!showTrace && level == Level.Trace) return;

            // BSIPA's own progress messages are noise unless diagnosing the loader; problems always show.
            // Hidden messages still reach the full log.
            bool hidden = loaderOwned && !essential && ((byte)level & ProblemLevels) == 0 && !showLoaderMessages && !SelfConfig.CommandLineValues.Debug.ShowLoaderMessages;
            if (hidden && fullPrinters.Count == 0) return;

            // make sure that the queue isn't being cleared
            logWaitEvent.Wait();
            try
            {
                var sync = syncLogging && !IsOnLoggerThread;
                if (sync)
                {
                    threadSync ??= new ManualResetEventSlim();
                    threadSync.Reset();
                }

                logQueue.Add(new LogMessage
                {
                    Level = level,
                    Message = message,
                    Logger = this,
                    Time = Utils.CurrentTime(),
                    Sync = threadSync,
                    Hidden = hidden
                });

                if (sync) threadSync!.Wait();
            }
            catch (InvalidOperationException)
            {
                // the queue has been closed, so we leave it
            }
        }

        [ThreadStatic]
        private static ManualResetEventSlim? threadSync;

        /// <inheritdoc />
        /// <summary>
        /// An override to <see cref="M:IPA.Logging.Logger.Debug(System.String)" /> which shows the method that called it.
        /// </summary>
        /// <param name="message">the message to log</param>
        public override void Debug(string message)
        {
            if (showSourceClass)
            {
                // add source to message
                var stackFrame = new StackTrace(true).GetFrame(1);
                var lineNo = stackFrame.GetFileLineNumber();

                if (lineNo == 0)
                { // no debug info
                    var method = stackFrame.GetMethod();
                    var paramString = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName));

                    message = $"{{{method.DeclaringType?.FullName}::{method.Name}({paramString})}} {message}";
                }
                else
                    message = $"{{{stackFrame.GetFileName()}:{lineNo}}} {message}";

            }

            base.Debug(message);
        }

        private struct LogMessage
        {
            public Level Level;
            public StandardLogger Logger;
            public string Message;
            public DateTime Time;
            public ManualResetEventSlim? Sync;
            public bool Hidden;
        }

        [ThreadStatic]
        private static bool? isOnLoggerThread;
        /// <summary>
        /// Whether or not the calling thread is the logger thread.
        /// </summary>
        /// <value><see langword="true"/> if the current thread is the logger thread, <see langword="false"/> otherwise</value>
        public static bool IsOnLoggerThread => isOnLoggerThread ??= Thread.CurrentThread.ManagedThreadId == logThread?.ManagedThreadId;

        private static readonly ManualResetEventSlim logWaitEvent = new(true);
        private static readonly BlockingCollection<LogMessage> logQueue = new();
        private static Thread? logThread;

        private static StandardLogger? loggerLogger;

        private const int LogCloseTimeout = 250;

        private static readonly HashSet<LogPrinter> startedPrinters = new();

        private static void EnsureStarted(LogPrinter printer)
        {
            if (startedPrinters.Add(printer))
                printer.StartPrint();
            printer.LastUse = Utils.CurrentTime();
        }

        private static IEnumerable<LogPrinter> PrintersFor(StandardLogger logger)
        {
            IEnumerable<LogPrinter> printers = logger.printers;
            for (var parent = logger.parent; parent != null; parent = parent.parent)
                printers = printers.Concat(parent.printers);
            return printers.Concat(defaultPrinters);
        }

        private static bool Accepts(LogPrinter printer, Level level) => ((byte)level & (byte)printer.Filter) != 0;

        private static void DispatchFull(LogMessage msg)
        {
            foreach (var printer in fullPrinters)
            {
                try
                {
                    if (!Accepts(printer, msg.Level)) continue;
                    EnsureStarted(printer);
                    printer.Print(msg.Level, msg.Time, msg.Logger.logName, msg.Message);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"printer errored: {e}");
                }
            }
        }

        private static void Dispatch(LogMessage msg)
        {
            foreach (var printer in PrintersFor(msg.Logger))
            {
                try
                {
                    if (!Accepts(printer, msg.Level)) continue;
                    EnsureStarted(printer);
                    printer.Print(msg.Level, msg.Time, msg.Logger.logName, msg.Message);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"printer errored: {e}");
                }
            }
        }

        /// <summary>
        /// Collapses repeated messages. A message identical (logger, level and text) to one of the
        /// last <see cref="RepeatWindow"/> distinct messages is not printed again; instead one
        /// summary line is written after its first repeat, and file printers rewrite that line's
        /// counter in place. Printers that cannot rewrite get the final count as a new line when
        /// the log goes idle or the message leaves the window.
        /// </summary>
        private static class RepeatCompactor
        {
            private const int RepeatWindow = 32;
            private const int SnippetLength = 100;
            private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);

            private sealed class Target
            {
                internal LogPrinter Printer = null!;
                internal object? Handle;
                internal int Reported;
            }

            private sealed class Group
            {
                internal string Key = "";
                internal Level Level;
                internal string LogName = "";
                internal string Prefix = "";
                internal int Count = 1;
                internal DateTime Last;
                internal bool Summarized;
                internal bool Dirty;
                internal DateTime LastWrite;
                internal readonly List<Target> Targets = new();
            }

            private static readonly LinkedList<Group> groups = new();
            private static readonly Dictionary<string, LinkedListNode<Group>> index = new();

            private static string KeyOf(LogMessage msg) => msg.Logger.logName + "\u0001" + (byte)msg.Level + "\u0001" + msg.Message;

            private static string Field(Group group)
                => ("x" + group.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).PadRight(11) + " (last " + group.Last.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ")";

            private static string PrefixOf(string message)
            {
                var line = message;
                int newline = line.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0) line = line.Substring(0, newline);
                line = GZFilePrinter.removeControlCodes.Replace(line, "");
                if (line.Length > SnippetLength) line = line.Substring(0, SnippetLength) + "...";
                return $"Compacted to avoid clutter, previous log \"{line}\" ";
            }

            /// <returns><see langword="true"/> if the message was absorbed as a repeat and must not be printed</returns>
            internal static bool Absorb(LogMessage msg)
            {
                var key = KeyOf(msg);
                if (index.TryGetValue(key, out var node))
                {
                    var group = node.Value;
                    group.Count++;
                    group.Last = msg.Time;
                    groups.Remove(node);
                    groups.AddFirst(node);
                    if (!group.Summarized)
                        WriteSummary(group, msg.Logger);
                    else
                        group.Dirty = true;
                    return true;
                }

                var added = groups.AddFirst(new Group { Key = key, Level = msg.Level, LogName = msg.Logger.logName, Last = msg.Time });
                index[key] = added;
                while (groups.Count > RepeatWindow)
                {
                    var oldest = groups.Last!;
                    Flush(oldest.Value, force: true);
                    index.Remove(oldest.Value.Key);
                    groups.RemoveLast();
                }
                return false;
            }

            private static void WriteSummary(Group group, StandardLogger logger)
            {
                group.Prefix = PrefixOf(KeyMessage(group));
                group.Summarized = true;
                group.LastWrite = Utils.CurrentTime();
                foreach (var printer in PrintersFor(logger))
                {
                    try
                    {
                        if (!Accepts(printer, group.Level)) continue;
                        EnsureStarted(printer);
                        var handle = printer.PrintUpdatable(group.Level, group.Last, group.LogName, group.Prefix, Field(group));
                        group.Targets.Add(new Target { Printer = printer, Handle = handle, Reported = group.Count });
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"printer errored: {e}");
                    }
                }
            }

            private static string KeyMessage(Group group)
            {
                int first = group.Key.IndexOf('\u0001');
                int second = group.Key.IndexOf('\u0001', first + 1);
                return group.Key.Substring(second + 1);
            }

            private static void Flush(Group group, bool force)
            {
                if (!group.Summarized) return;
                var now = Utils.CurrentTime();
                if (group.Dirty && (force || now - group.LastWrite >= UpdateInterval))
                {
                    foreach (var target in group.Targets)
                    {
                        if (target.Handle == null) continue;
                        try
                        {
                            EnsureStarted(target.Printer);
                            target.Printer.UpdateField(target.Handle, Field(group));
                            target.Reported = group.Count;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"printer errored: {e}");
                        }
                    }
                    group.Dirty = false;
                    group.LastWrite = now;
                }
                if (!force) return;
                foreach (var target in group.Targets)
                {
                    if (target.Handle != null || target.Reported >= group.Count) continue;
                    try
                    {
                        EnsureStarted(target.Printer);
                        target.Printer.Print(group.Level, group.Last, group.LogName, group.Prefix + Field(group));
                        target.Reported = group.Count;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"printer errored: {e}");
                    }
                }
            }

            internal static void FlushAll(bool force)
            {
                foreach (var group in groups)
                    Flush(group, force);
            }
        }

        /// <summary>
        /// The log printer thread for <see cref="StandardLogger"/>.
        /// </summary>
        private static void LogThread()
        {
            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                StopLogThread();
            };

            loggerLogger = new StandardLogger("Log Subsystem");
            loggerLogger.printers.Clear(); // don't need a log file for this one

            var timeout = TimeSpan.FromMilliseconds(LogCloseTimeout);

            try
            {
                while (logQueue.TryTake(out var msg, Timeout.Infinite))
                {
                    StdoutInterceptor.Intercept(); // only runs once, after the first message is queued
                    do
                    {
                        DispatchFull(msg);
                        if (!msg.Hidden && !RepeatCompactor.Absorb(msg))
                            Dispatch(msg);
                        RepeatCompactor.FlushAll(force: false);

                        msg.Sync?.Set();

                        var debugConfig = SelfConfig.Instance?.Debug;

                        if (debugConfig != null && debugConfig.HideMessagesForPerformance
                            && logQueue.Count > debugConfig.HideLogThreshold)
                        { // spam filtering (if queue has more than HideLogThreshold elements)
                            logWaitEvent.Reset(); // pause incoming log requests

                            // clear loggers for this instance, to print the message to all affected logs
                            loggerLogger.printers.Clear();
                            var prints = new HashSet<LogPrinter>();
                            // clear the queue
                            while (logQueue.TryTake(out var message))
                            { // aggregate loggers in the process
                                var messageLogger = message.Logger;
                                foreach (var print in messageLogger.printers)
                                    _ = prints.Add(print);
                                do
                                {
                                    messageLogger = messageLogger.parent;
                                    if (messageLogger != null)
                                        foreach (var print in messageLogger.printers)
                                            _ = prints.Add(print);
                                } while (messageLogger != null);

                                message.Sync?.Set();
                            }

                            // print using logging subsystem to all logger printers
                            loggerLogger.printers.AddRange(prints);
                            logQueue.Add(new LogMessage
                            { // manually adding to the queue instead of using Warn() because calls to the logger are suspended here
                                Level = Level.Warning,
                                Logger = loggerLogger,
                                Message = $"{loggerLogger.logName.ToUpperInvariant()}: Messages omitted to improve performance",
                                Time = Utils.CurrentTime()
                            });

                            // resume log calls
                            logWaitEvent.Set();
                        }

                        var now = Utils.CurrentTime();
                        foreach (var printer in new List<LogPrinter>(startedPrinters))
                        {
                            // close printer after 500ms from its last use
                            if (now - printer.LastUse > timeout)
                                EndPrinter(printer);
                        }
                    }
                    // wait for messages for 500ms before ending the prints
                    while (logQueue.TryTake(out msg, timeout));

                    if (logQueue.Count == 0)
                    { // when the queue has been empty for 500ms, publish pending counts and end all prints
                        RepeatCompactor.FlushAll(force: true);
                        foreach (var printer in new List<LogPrinter>(startedPrinters))
                            EndPrinter(printer);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }

            RepeatCompactor.FlushAll(force: true);
            foreach (var printer in new List<LogPrinter>(startedPrinters))
                EndPrinter(printer);
        }

        private static void EndPrinter(LogPrinter printer)
        {
            try
            {
                printer.EndPrint();
            }
            catch (Exception e)
            {
                Console.WriteLine($"printer errored: {e}");
            }
            _ = startedPrinters.Remove(printer);
        }

        /// <summary>
        /// Stops and joins the log printer thread.
        /// </summary>
        internal static void StopLogThread()
        {
            logQueue.CompleteAdding();
            logThread!.Join();
        }
    }

    /// <summary>
    /// A class providing extensions for various loggers.
    /// </summary>
    public static class LoggerExtensions
    {
        /// <summary>
        /// Gets a child logger, if supported. Currently the only defined and supported logger is <see cref="StandardLogger"/>, and most plugins will only ever receive this anyway.
        /// </summary>
        /// <param name="logger">the parent <see cref="Logger"/></param>
        /// <param name="name">the name of the child</param>
        /// <returns>the child logger</returns>
        public static Logger GetChildLogger(this Logger logger, string name)
            => logger switch
            {
                StandardLogger l => l.GetChild(name),
                _ => throw new InvalidOperationException()
            };
    }
}
