using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace IPA.Logging
{
    // https://stackoverflow.com/a/48864902/3117125
    internal static class WinConsole
    {
        internal static TextWriter ConOut;
        internal static TextReader ConIn;

        private static SafeFileHandle outHandle;
        private static SafeFileHandle inHandle;

        public static bool UseVTEscapes { get; private set; }

        internal static IntPtr OutHandle => outHandle == null || outHandle.IsClosed || outHandle.IsInvalid ? IntPtr.Zero : outHandle.DangerousGetHandle();
        internal static IntPtr InHandle => inHandle == null || inHandle.IsClosed || inHandle.IsInvalid ? IntPtr.Zero : inHandle.DangerousGetHandle();

        internal static bool IsInitialized;
        private static bool ownsConsole;

        public static void Initialize(int processId, bool alwaysCreateNewConsole = false)
        {
            if (IsInitialized) return;
            var args = Environment.GetCommandLineArgs();
            if (!alwaysCreateNewConsole && processId == AttachParent && !args.Contains("--legacy-console")
                && DefaultTerminalConsole.TryAttach())
                ownsConsole = true;
            else if (alwaysCreateNewConsole || !AttachConsole(processId))
                ownsConsole |= AllocConsole();

            // An already attached process can fail both AttachConsole and AllocConsole.
            IsInitialized = InitializeStreams();
            if (IsInitialized && DefaultTerminalConsole.FailureReason != null)
                Console.Error.WriteLine("Using the original console: " + DefaultTerminalConsole.FailureReason);
        }

        private static bool InitializeStreams()
        {
            if (!InitializeOutStream()) return false;
            InitializeInStream();
            return true;
        }

        private static bool InitializeOutStream()
        {
            var fs = CreateFileStream("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, FileAccess.Write, out outHandle)
                ?? CreateFileStream("CONOUT$", GenericWrite, FileShareRead | FileShareWrite, FileAccess.Write, out outHandle);
            if (fs != null)
            {
                var writer = new StreamWriter(fs) { AutoFlush = true };
                ConOut = writer;
                Console.SetOut(writer);
                Console.SetError(writer);
                _ = SetStdHandle(-11, OutHandle);
                _ = SetStdHandle(-12, OutHandle);

                var handle = OutHandle;
                if (GetConsoleMode(handle, out var mode))
                {
                    mode |= EnableProcessedOutput | EnableVTProcessing;
                    UseVTEscapes = SetConsoleMode(handle, mode);
                    if (!UseVTEscapes)
                    {
                        Console.Error.WriteLine("VT console output unavailable; using plain output: " +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    }
                }
                else
                {
                    UseVTEscapes = false;
                    Console.Error.WriteLine("VT console output unavailable; using plain output: " +
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
                return true;
            }
            return false;
        }

        private static void InitializeInStream()
        {
            var fs = CreateFileStream("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, FileAccess.Read, out inHandle)
                ?? CreateFileStream("CONIN$", GenericRead, FileShareRead | FileShareWrite, FileAccess.Read, out inHandle);
            if (fs != null)
            {
                if (ownsConsole && GetConsoleMode(InHandle, out var mode))
                    _ = SetConsoleMode(InHandle, mode | EnableExtendedFlags | EnableQuickEdit);
                Console.SetIn(ConIn = new StreamReader(fs));
                _ = SetStdHandle(-10, InHandle);
            }
        }

        private static FileStream CreateFileStream(string name, uint win32DesiredAccess, uint win32ShareMode,
                                FileAccess dotNetFileAccess, out SafeFileHandle handle)
        {
            var file = new SafeFileHandle(CreateFile(name, win32DesiredAccess, win32ShareMode, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero), true);
            if (!file.IsInvalid)
            {
                handle = file;
                var fs = new FileStream(file, dotNetFileAccess);
                return fs;
            }

            file.Dispose();
            handle = null;
            return null;
        }

        #region Win API Functions and Constants

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetStdHandle(int standardHandle, IntPtr handle);

        private const uint EnableProcessedOutput = 0x0001;
        private const uint EnableVTProcessing = 0x0004;
        private const uint EnableQuickEdit = 0x0040;
        private const uint EnableExtendedFlags = 0x0080;

        private const uint GenericWrite = 0x40000000;
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 0x00000003;
        private const uint FileAttributeNormal = 0x80;

        internal const int AttachParent = -1;

        #endregion
    }
}
