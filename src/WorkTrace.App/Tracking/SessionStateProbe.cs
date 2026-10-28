using WorkTrace.App.Interop;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// Asks Windows whether this session's desktop is currently usable: while the session is locked (or
/// disconnected) the input desktop is the secure desktop, which a normal process cannot switch to.
/// </summary>
internal sealed class SessionStateProbe : ISessionStateProbe
{
    public bool? IsSessionLocked()
    {
        try
        {
            var desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_SWITCHDESKTOP);
            if (desktop == IntPtr.Zero)
            {
                return true;
            }

            try
            {
                return !NativeMethods.SwitchDesktop(desktop);
            }
            finally
            {
                NativeMethods.CloseDesktop(desktop);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
