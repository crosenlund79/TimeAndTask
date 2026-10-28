using WorkTrace.App.Interop;

namespace WorkTrace.App.Shell;

/// <summary>
/// Ensures one WorkTrace instance per Windows session. A second launch signals the running
/// instance (which then shows its main window) and exits.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    // "Local\" scopes the objects to the current logon session.
    private const string MutexName = @"Local\WorkTrace.SingleInstance.6E1B9F4A";
    private const string ActivateEventName = @"Local\WorkTrace.Activate.6E1B9F4A";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _registration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    /// <summary>Returns a guard if this is the first instance; otherwise null.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstanceGuard(mutex, activateEvent);
    }

    /// <summary>Asks the running instance to show itself. Returns false if it could not be reached.</summary>
    public static bool SignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
        {
            return false;
        }

        using (handle)
        {
            // We were started by the user and may grant foreground rights to the running instance.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            return handle.Set();
        }
    }

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever another launch signals.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activateEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
