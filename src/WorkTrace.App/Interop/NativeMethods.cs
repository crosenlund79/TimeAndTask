using System.Runtime.InteropServices;

namespace WorkTrace.App.Interop;

internal static partial class NativeMethods
{
    /// <summary>Allows any process to set the foreground window (used before signalling the running instance).</summary>
    internal const int ASFW_ANY = -1;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int dwProcessId);
}
