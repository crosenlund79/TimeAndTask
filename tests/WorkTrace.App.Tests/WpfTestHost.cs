using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace WorkTrace.App.Tests;

/// <summary>
/// Runs test code on a single long-lived STA thread hosting the real <see cref="App"/> resources
/// (Fluent theme + WorkTrace styles). WPF allows only one Application per process.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> UiDispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static BindingErrorListener BindingErrors { get; } = new();

    /// <summary>Exceptions that escaped to the dispatcher (e.g. XAML load failures during rendering).</summary>
    public static List<Exception> UnhandledExceptions { get; } = [];

    public static Task RunAsync(Func<Task> test) =>
        UiDispatcher.Value.InvokeAsync(test).Task.Unwrap();

    /// <summary>Lets pending bindings, layout and async continuations run.</summary>
    public static async Task SettleAsync(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            await Task.Delay(40);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) =>
            {
                UnhandledExceptions.Add(e.Exception);
                e.Handled = true;
            };

            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>Collects WPF data-binding errors ("System.Windows.Data Error: 40 ...").</summary>
internal sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Take()
    {
        lock (_errors)
        {
            var copy = _errors.ToList();
            _errors.Clear();
            return copy;
        }
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (message is null)
        {
            return;
        }

        lock (_errors)
        {
            _errors.Add(message);
        }
    }
}
