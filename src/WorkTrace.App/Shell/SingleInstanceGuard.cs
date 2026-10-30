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

    // Set by the installer (installer/WorkTrace.iss) to close the running instance before replacing files.
    private const string ExitEventName = @"Local\WorkTrace.Exit.6E1B9F4A";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly EventWaitHandle _exitEvent;
    private RegisteredWaitHandle? _registration;
    private RegisteredWaitHandle? _exitRegistration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent, EventWaitHandle exitEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _exitEvent = exitEvent;
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
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        return new SingleInstanceGuard(mutex, activateEvent, exitEvent);
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

    /// <summary>Invokes <paramref name="onExit"/> (on a thread-pool thread) when the installer asks WorkTrace to close.</summary>
    public void ListenForExit(Action onExit)
    {
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent, (_, _) => onExit(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _exitRegistration?.Unregister(null);
        _activateEvent.Dispose();
        _exitEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
