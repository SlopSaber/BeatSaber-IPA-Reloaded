using System;
using System.Linq;
using IPA.Config;

namespace IPA.Desktop
{
    internal static class DesktopMode
    {
        internal static bool Requested
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                return !args.Contains("--no-fpfc") && (SelfConfig.Instance.EnableFPFC
                    || args.Any(argument => argument == "fpfc" || argument == "-fpfc" || argument == "--fpfc"));
            }
        }
    }
}
