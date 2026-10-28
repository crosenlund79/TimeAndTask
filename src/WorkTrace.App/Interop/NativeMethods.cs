using System.Runtime.InteropServices;

namespace WorkTrace.App.Interop;

internal static partial class NativeMethods
{
    /// <summary>Allows any process to set the foreground window (used before signalling the running instance).</summary>
    internal const int ASFW_ANY = -1;

    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_ROUND = 2;

    internal const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SwitchDesktop(IntPtr hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseDesktop(IntPtr hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int dwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SW_SHOW = 5;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    internal static partial void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fUnknown);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, [In] MouseInputRecord[] pInputs, int cbSize);

    /// <summary>INPUT with a MOUSEINPUT payload (the largest member of the native union).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputRecord
    {
        public uint Type;
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>How <see cref="ForceForeground"/> succeeded (for diagnostics), or <see cref="Failed"/>.</summary>
    internal enum ForegroundResult
    {
        AlreadyForeground,
        SetForegroundWindow,
        AttachThreadInput,
        SyntheticInput,
        SwitchToThisWindow,
        Failed,
    }

    /// <summary>
    /// Brings a window to the foreground even when another process is active. Windows only lets a process
    /// take the foreground if it produced the latest input (foreground lock). Steps, each verified:
    /// plain SetForegroundWindow; attach to the foreground thread's input queue; send a zero-movement mouse
    /// input so this process owns the latest input (no visible effect; the technique used by launchers and
    /// tray utilities); finally SwitchToThisWindow.
    /// </summary>
    internal static ForegroundResult ForceForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd)
        {
            return ForegroundResult.AlreadyForeground;
        }

        ShowWindow(hwnd, SW_SHOW);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
        {
            return ForegroundResult.SetForegroundWindow;
        }

        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (GetForegroundWindow() == hwnd)
            {
                return ForegroundResult.AttachThreadInput;
            }
        }

        var dummy = new[] { new MouseInputRecord { Type = 0 /* INPUT_MOUSE */ } };
        if (SendInput(1, dummy, Marshal.SizeOf<MouseInputRecord>()) == 1)
        {
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() == hwnd)
            {
                return ForegroundResult.SyntheticInput;
            }
        }

        SwitchToThisWindow(hwnd, true);
        return GetForegroundWindow() == hwnd ? ForegroundResult.SwitchToThisWindow : ForegroundResult.Failed;
    }

    /// <summary>Hides a window from Alt+Tab and gives it rounded corners on Windows 11 (no-op elsewhere).</summary>
    internal static void StylePopup(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);

        var corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }
}
