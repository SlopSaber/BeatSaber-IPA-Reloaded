using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace IPA.Logging
{
    internal static class DefaultTerminalConsole
    {
        internal static string FailureReason { get; private set; }

        internal static bool TryAttach()
        {
            Process host = null;
            bool attached = false;
            try
            {
                string executable = Path.Combine(Path.GetDirectoryName(typeof(WinConsole).Assembly.Location), "IPA.Console.exe");
                if (!File.Exists(executable)) return false;
                using var game = Process.GetCurrentProcess();
                string eventName = @"Local\BSIPA.Console." + game.Id + "." + Guid.NewGuid().ToString("N");
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                host = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = game.Id + " " + eventName,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                });
                if (host == null || !ready.WaitOne(5000) || host.HasExited)
                {
                    FailureReason = "Console host did not become ready within five seconds.";
                    return false;
                }
                if (!FreeConsole())
                {
                    FailureReason = "FreeConsole failed with Win32 error " + Marshal.GetLastWin32Error();
                    return false;
                }
                attached = AttachConsole(host.Id);
                if (!attached) FailureReason = "AttachConsole failed with Win32 error " + Marshal.GetLastWin32Error();
                return attached;
            }
            catch (Exception error)
            {
                // Library resolution and the logger are not initialized yet.
                FailureReason = error.Message;
                return false;
            }
            finally
            {
                if (host != null)
                {
                    if (!attached)
                    {
                        try { if (!host.HasExited) host.Kill(); }
                        catch { }
                    }
                    host.Dispose();
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool FreeConsole();
    }
}
