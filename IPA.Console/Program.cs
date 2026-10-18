using System;
using System.Diagnostics;
using System.Threading;

namespace IPA.ConsoleHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2 || !int.TryParse(args[0], out var gameId)) return 1;
            try
            {
                using var ready = EventWaitHandle.OpenExisting(args[1]);
                using var game = Process.GetProcessById(gameId);
                Console.Title = "BSIPA - Game Console";
                ready.Set();
                game.WaitForExit();
                return 0;
            }
            catch
            {
                return 1;
            }
        }
    }
}
