using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IPA.Desktop
{
    internal sealed class DesktopMonitor
    {
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        private readonly WindowCallback findWindow;
        private readonly StringBuilder className = new(256);
        private IntPtr window;

        internal DesktopMonitor() { findWindow = FindWindow; }

        internal bool TryGetRefreshRate(out int refreshRate, out string device)
        {
            refreshRate = 0;
            device = null;
            if (window == IntPtr.Zero || !IsWindow(window))
            {
                window = IntPtr.Zero;
                EnumThreadWindows(GetCurrentThreadId(), findWindow, IntPtr.Zero);
            }
            if (window == IntPtr.Zero) return false;
            IntPtr monitor = MonitorFromWindow(window, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
            if (!EnumDisplaySettings(info.DeviceName, -1, ref mode) || mode.Frequency <= 1 || mode.Frequency > 10000) return false;
            refreshRate = (int)mode.Frequency;
            device = info.DeviceName;
            return true;
        }

        private bool FindWindow(IntPtr candidate, IntPtr parameter)
        {
            className.Clear();
            if (GetClassName(candidate, className, className.Capacity) == 0 || className.ToString() != "UnityWndClass") return true;
            window = candidate;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            // The display branch of DEVMODEW's union occupies 16 bytes.
            public int PositionX, PositionY;
            public uint Orientation, FixedOutput;
            public short Color, Duplex, YResolution, TrueTypeOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr parameter);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string device, int modeIndex, ref DisplayMode mode);
    }
}
