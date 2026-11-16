using System.Runtime.InteropServices;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Interop;

/// <summary>
/// Keyboard/mouse idle time from Windows' last-input counter (GetLastInputInfo), read only when a check-in is due:
/// no hooks, no input recorded, no polling.
/// </summary>
internal sealed class UserIdleProbe : IUserIdleProbe
{
    public TimeSpan? GetIdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return null; // unknown counts as idle: a check-in is never held back indefinitely
        }

        // Both are 32-bit millisecond tick counts; unsigned subtraction handles the 49.7-day wrap-around.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
