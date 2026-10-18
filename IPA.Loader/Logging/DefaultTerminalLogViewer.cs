using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace IPA.Logging
{
    internal static class DefaultTerminalLogViewer
    {
        private static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        internal static bool IsAvailable => File.Exists(PowerShellPath);

        internal static bool TryOpen(string path)
        {
            try
            {
                using var game = Process.GetCurrentProcess();
                string script = "$logPath='" + path.Replace("'", "''") + "'; $gameId=" + game.Id + ";\n" + ViewerScript;
                using var viewer = Process.Start(new ProcessStartInfo
                {
                    FileName = PowerShellPath,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                    WorkingDirectory = Path.GetDirectoryName(path),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                });
                return viewer != null;
            }
            catch (Exception error)
            {
                Logger.Default.Warn($"Could not open the default terminal log viewer; using the native console: {error.Message}");
                return false;
            }
        }

        private const string ViewerScript = @"
$ErrorActionPreference='Stop'
try { $game=[Diagnostics.Process]::GetProcessById($gameId) } catch { exit }
[Console]::Title='BSIPA - Live Log'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
$originalColor=[Console]::ForegroundColor
$reader=$null
try {
    $stream=[IO.File]::Open($logPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=New-Object System.IO.StreamReader($stream)
    $pending=''
    while($true) {
        $finished=$game.HasExited
        $pending += $reader.ReadToEnd()
        $lines=$pending.Split([char]10)
        for($i=0; $i -lt $lines.Length-1; $i++) {
            $line=$lines[$i].TrimEnd([char]13)
            [Console]::ForegroundColor=switch -Regex ($line) {
                '^\[TRACE ' {'DarkMagenta';break}
                '^\[DEBUG ' {'Green';break}
                '^\[INFO ' {'White';break}
                '^\[NOTICE ' {'Cyan';break}
                '^\[WARNING ' {'Yellow';break}
                '^\[ERROR ' {'Red';break}
                '^\[CRITICAL ' {'Magenta';break}
                default {$originalColor}
            }
            [Console]::WriteLine($line)
        }
        $pending=$lines[-1]
        if($finished) {
            if($pending.Length -gt 0) { [Console]::Write($pending) }
            break
        }
        Start-Sleep -Milliseconds 100
    }
} finally {
    if($reader) { $reader.Dispose() }
    $game.Dispose()
    [Console]::ForegroundColor=$originalColor
}
";
    }
}
