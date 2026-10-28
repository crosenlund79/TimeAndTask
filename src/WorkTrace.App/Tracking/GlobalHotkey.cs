using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Interop;

namespace WorkTrace.App.Tracking;

/// <summary>
/// One system-wide keyboard shortcut (RegisterHotKey) delivered to a message-only window on the UI thread.
/// </summary>
internal sealed class GlobalHotkey(ILogger<GlobalHotkey> logger) : IDisposable
{
    private const int HotkeyId = 0x5754; // "WT"

    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    /// <summary>The shortcut currently registered, or null.</summary>
    public string? Current { get; private set; }

    /// <summary>Parses a shortcut such as "Ctrl+Alt+Space". Requires at least one modifier.</summary>
    internal static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(text.Trim()) is KeyGesture gesture
                && gesture.Modifiers != ModifierKeys.None)
            {
                modifiers = gesture.Modifiers;
                key = gesture.Key;
                return true;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
        }

        return false;
    }

    /// <summary>Registers (or re-registers) the shortcut. Returns false when invalid or already taken by another app.</summary>
    public bool Register(string? shortcut)
    {
        Unregister();
        if (!TryParse(shortcut, out var modifiers, out var key))
        {
            logger.LogWarning("Global shortcut is not valid");
            return false;
        }

        if (_source is null)
        {
            _source = new HwndSource(new HwndSourceParameters("WorkTraceHotkeys") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
            _source.AddHook(WndProc);
        }

        var flags = NativeMethods.MOD_NOREPEAT
            | (modifiers.HasFlag(ModifierKeys.Alt) ? NativeMethods.MOD_ALT : 0)
            | (modifiers.HasFlag(ModifierKeys.Control) ? NativeMethods.MOD_CONTROL : 0)
            | (modifiers.HasFlag(ModifierKeys.Shift) ? NativeMethods.MOD_SHIFT : 0)
            | (modifiers.HasFlag(ModifierKeys.Windows) ? NativeMethods.MOD_WIN : 0);
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, flags, vk);
        if (!_registered)
        {
            logger.LogWarning("Global shortcut {Shortcut} is in use by another application", shortcut);
            return false;
        }

        Current = shortcut!.Trim();
        logger.LogInformation("Global shortcut {Shortcut} registered", Current);
        return true;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
