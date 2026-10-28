using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>A task offered in the popup (search result or "create new task").</summary>
public sealed record SuggestionItem(TaskSummary? Task, string Title, string? Context, string? ExternalId)
{
    public bool IsCreateNew => Task is null;

    public bool HasExternalId => !string.IsNullOrEmpty(ExternalId);

    public static SuggestionItem For(TaskSummary task) =>
        new(task, task.Title, task.TaskSystemName is { } s ? $"{task.ClientName} · {s}" : task.ClientName, task.ExternalId);
}

/// <summary>A recently used task with its Alt+number shortcut.</summary>
public sealed record RecentTaskItem(TaskSummary Task, int Number)
{
    public string Title => Task.Title;

    public string Shortcut => $"Alt+{Number}";

    public string ToolTip => $"{Task.Title} — {Task.ClientName}{(Task.ExternalId is { } id ? $" · {id}" : string.Empty)}\nClick or press {Shortcut} to record";
}

/// <summary>
/// The check-in popup. Designed for the fewest possible keystrokes:
/// Enter on an empty search repeats the previous task; Alt+1…4 records a recent task; typing searches
/// and Enter records the highlighted match (or creates the typed task). Everything else is optional.
/// </summary>
public sealed partial class CheckInViewModel(
    ITaskService tasks,
    IClientService clients,
    ILookupService lookups,
    ICheckInService checkIns,
    ISettingsStore settings,
    TimeProvider timeProvider,
    ILogger<CheckInViewModel> logger) : ObservableObject, IDisposable
{
    private const int MaxSuggestions = 6;
    private const int MaxRecent = 4;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(120);

    private int? _promptId;
    private string _initialTimeText = string.Empty;
    private DateTime _initialDate;
    private CancellationTokenSource? _searchCts;
    private Task _searchTask = Task.CompletedTask;
    private IReadOnlyList<ClientSummary> _clients = [];
    private bool _hovered;

    /// <summary>Raised when the popup should close, with how it ended.</summary>
    public event EventHandler<PromptOutcome>? CloseRequested;

    /// <summary>Raised after an observation was stored.</summary>
    public event EventHandler<ObservationSummary>? Recorded;

    public string Heading { get; private set; } = "What are you working on?";

    public string Subheading { get; private set; } = "Record activity";

    public bool IsScheduled => _promptId is not null;

    public ObservableCollection<RecentTaskItem> Recent { get; } = [];

    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    public ObservableCollection<PickerOption> ClientOptions { get; } = [];

    public ObservableCollection<PickerOption> TaskSystemOptions { get; } = [];

    public ObservableCollection<PickerOption> ChannelOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious), nameof(PreviousContext), nameof(PendingSummary))]
    public partial TaskSummary? PreviousTask { get; private set; }

    public bool HasPrevious => PreviousTask is not null;

    public string? PreviousContext => PreviousTask is { } t
        ? (t.ExternalId is { } id ? $"{t.ClientName} · {id}" : t.ClientName)
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(PendingSummary))]
    public partial string? SearchText { get; set; }

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingSummary))]
    public partial SuggestionItem? SelectedSuggestion { get; set; }

    /// <summary>Client filter for search and owner of a newly created task (0 = any / not chosen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingSummary))]
    public partial int? ClientKey { get; set; } = PickerOption.NoneKey;

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial int? TaskSystemKey { get; set; } = PickerOption.NoneKey;

    [ObservableProperty]
    public partial string? ExternalId { get; set; }

    [ObservableProperty]
    public partial int? ChannelKey { get; set; } = PickerOption.NoneKey;

    [ObservableProperty]
    public partial DateTime ObservedDate { get; set; }

    [ObservableProperty]
    public partial string? ObservedTimeText { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ReusePreviousCommand), nameof(PickRecentCommand), nameof(PickRecentByNumberCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownText))]
    public partial int SecondsRemaining { get; private set; }

    [ObservableProperty]
    public partial int CountdownTotal { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountdownText))]
    public partial bool IsCountdownActive { get; private set; }

    public string CountdownText => IsCountdownActive
        ? $"Closes in {SecondsRemaining / 60}:{SecondsRemaining % 60:00} unless you interact"
        : string.Empty;

    /// <summary>Plain-language preview of what Enter will record.</summary>
    public string PendingSummary
    {
        get
        {
            if (SelectedSuggestion is { IsCreateNew: true })
            {
                return $"Enter creates “{Text.Clean(SearchText)}” for {NewTaskClientName ?? "the chosen client"}";
            }

            if (SelectedSuggestion?.Task is { } task)
            {
                return $"Enter records “{task.Title}”";
            }

            if (!IsSearching && PreviousTask is { } previous)
            {
                return $"Enter records “{previous.Title}” again";
            }

            return IsSearching ? "No matching task" : "Type to search, or write a note";
        }
    }

    private string? NewTaskClientName => _clients.FirstOrDefault(c => c.Id == NewTaskClientId)?.Name;

    /// <summary>Owner of a task created from the popup: the chosen client, else the previous task's, else the first active one.</summary>
    private int? NewTaskClientId =>
        PickerOption.FromKey(ClientKey ?? 0)
        ?? PreviousTask?.ClientId
        ?? _clients.FirstOrDefault(c => c.IsActive)?.Id;

    /// <param name="promptId">The scheduled prompt being answered; null for a manual check-in.</param>
    /// <param name="autoDismissSeconds">Countdown for scheduled prompts; null for none.</param>
    public void Configure(int? promptId, DateTime? scheduledAtUtc, PromptReason reason, int? autoDismissSeconds)
    {
        _promptId = promptId;
        Subheading = promptId is null
            ? "Record activity"
            : reason switch
            {
                PromptReason.TrackingStarted => "Tracking started",
                PromptReason.Resumed => "Welcome back",
                PromptReason.WelcomeBack => "You were away",
                _ => $"Check-in · {ToLocal(scheduledAtUtc ?? UtcTime.Now(timeProvider)):HH:mm}",
            };

        if (autoDismissSeconds is > 0)
        {
            CountdownTotal = autoDismissSeconds.Value;
            SecondsRemaining = autoDismissSeconds.Value;
            IsCountdownActive = true;
        }

        var now = ToLocal(UtcTime.Now(timeProvider));
        _initialDate = now.Date;
        _initialTimeText = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ObservedDate = _initialDate;
        ObservedTimeText = _initialTimeText;
    }

    public async Task LoadAsync()
    {
        try
        {
            _clients = await clients.GetClientsForPickerAsync();
            ClientOptions.Clear();
            ClientOptions.Add(new PickerOption(PickerOption.NoneKey, "Any client"));
            foreach (var client in _clients)
            {
                ClientOptions.Add(new PickerOption(client.Id, client.Name));
            }

            TaskSystemOptions.Clear();
            TaskSystemOptions.Add(new PickerOption(PickerOption.NoneKey, "None"));
            foreach (var system in await lookups.GetAsync(LookupKind.TaskSystem, includeInactive: false))
            {
                TaskSystemOptions.Add(new PickerOption(system.Id, system.Name));
            }

            ChannelOptions.Clear();
            ChannelOptions.Add(new PickerOption(PickerOption.NoneKey, "Not specified"));
            foreach (var channel in await lookups.GetAsync(LookupKind.CommunicationChannel, includeInactive: false))
            {
                ChannelOptions.Add(new PickerOption(channel.Id, channel.Name));
            }

            if (settings.Current.Tracking.RememberLastTask
                && await checkIns.GetLastObservationAsync() is { TaskId: { } lastTaskId })
            {
                var last = await tasks.GetAsync(lastTaskId);
                PreviousTask = last.IsArchived || !last.ClientIsActive ? null : last;
            }

            var recent = await tasks.SearchAsync(new TaskQuery(Limit: MaxRecent + 1));
            Recent.Clear();
            foreach (var task in recent.Where(t => t.Id != PreviousTask?.Id).Take(MaxRecent))
            {
                Recent.Add(new RecentTaskItem(task, Recent.Count + 1));
            }

            OnPropertyChanged(nameof(PendingSummary));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading the check-in popup failed");
            ErrorMessage = "Could not load your tasks. You can still write a note.";
        }
    }

    /// <summary>Called once per second by the window.</summary>
    public void Tick()
    {
        if (!IsCountdownActive || _hovered || IsBusy)
        {
            return;
        }

        SecondsRemaining = Math.Max(0, SecondsRemaining - 1);
        if (SecondsRemaining == 0)
        {
            IsCountdownActive = false;
            CloseRequested?.Invoke(this, PromptOutcome.TimedOut);
        }
    }

    /// <summary>The user typed or clicked: never auto-close under their hands.</summary>
    public void NotifyInteraction() => IsCountdownActive = false;

    /// <summary>Pointer over the popup pauses the countdown.</summary>
    public void SetHovered(bool hovered) => _hovered = hovered;

    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var index = SelectedSuggestion is null ? -1 : Suggestions.IndexOf(SelectedSuggestion);
        index = Math.Clamp(index + delta, 0, Suggestions.Count - 1);
        SelectedSuggestion = Suggestions[index];
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }

    partial void OnSearchTextChanged(string? value) => ScheduleSearch();

    partial void OnClientKeyChanged(int? value) => ScheduleSearch();

    partial void OnNoteChanged(string? value) => ErrorMessage = null;

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private Task SaveAsync()
    {
        if (SelectedSuggestion is { IsCreateNew: true })
        {
            return RecordAsync(task: null, newTaskTitle: Text.Clean(SearchText));
        }

        if (SelectedSuggestion?.Task is { } task)
        {
            return RecordAsync(task, null);
        }

        if (!IsSearching && PreviousTask is { } previous)
        {
            return RecordAsync(previous, null);
        }

        if (IsSearching)
        {
            ErrorMessage = "No matching task. Pick a suggestion or clear the search.";
            return Task.CompletedTask;
        }

        // Nothing chosen: a note-only check-in (or a validation message when the note is empty too).
        return RecordAsync(null, null);
    }

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private Task ReusePreviousAsync() => PreviousTask is { } previous ? RecordAsync(previous, null) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private Task PickRecentAsync(RecentTaskItem? item) => item is null ? Task.CompletedTask : RecordAsync(item.Task, null);

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private Task PickRecentByNumberAsync(string? number) =>
        int.TryParse(number, out var n) && Recent.FirstOrDefault(r => r.Number == n) is { } item
            ? RecordAsync(item.Task, null)
            : Task.CompletedTask;

    private bool CanRecord() => !IsBusy;

    [RelayCommand]
    private void Dismiss() => CloseRequested?.Invoke(this, PromptOutcome.Dismissed);

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    private async Task RecordAsync(TaskSummary? task, string? newTaskTitle)
    {
        NotifyInteraction();
        ErrorMessage = null;
        if (!TryGetObservedAt(out var observedAtUtc))
        {
            IsExpanded = true;
            ErrorMessage = "Enter the time as HH:mm, e.g. 09:45.";
            return;
        }

        IsBusy = true;
        try
        {
            if (newTaskTitle is not null)
            {
                if (NewTaskClientId is not { } clientId)
                {
                    ErrorMessage = "Choose a client for the new task.";
                    return;
                }

                task = await tasks.CreateAsync(new TaskInput(
                    clientId,
                    PickerOption.FromKey(TaskSystemKey ?? 0),
                    ExternalId,
                    newTaskTitle,
                    null,
                    PickerOption.FromKey(ChannelKey ?? 0)));
            }

            var channelId = PickerOption.FromKey(ChannelKey ?? 0) ?? task?.DefaultChannelId;
            var observation = await checkIns.RecordAsync(new CheckInRequest(_promptId, task?.Id, Note, channelId, observedAtUtc));
            Recorded?.Invoke(this, observation);
            CloseRequested?.Invoke(this, PromptOutcome.Answered);
        }
        catch (DomainValidationException ex)
        {
            ErrorMessage = ex.Errors.ByField.SelectMany(e => e.Value).FirstOrDefault() ?? ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recording a check-in failed");
            ErrorMessage = "Could not save. See the log for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Null when the user kept the default time ("now").</summary>
    private bool TryGetObservedAt(out DateTime? observedAtUtc)
    {
        observedAtUtc = null;
        var text = ObservedTimeText?.Trim() ?? string.Empty;
        if (text == _initialTimeText && ObservedDate.Date == _initialDate)
        {
            return true;
        }

        if (!TimeOnly.TryParseExact(text, ["HH:mm", "H:mm", "HHmm", "H.mm", "HH.mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        var local = DateTime.SpecifyKind(ObservedDate.Date + time.ToTimeSpan(), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        observedAtUtc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return true;
    }

    private void ScheduleSearch()
    {
        ErrorMessage = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        _searchTask = SearchAsync(_searchCts.Token);
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = Text.Clean(SearchText);
            if (text is null)
            {
                Suggestions.Clear();
                SelectedSuggestion = null;
                return;
            }

            await Task.Delay(SearchDelay, cancellationToken);
            var results = await tasks.SearchAsync(new TaskQuery(text, PickerOption.FromKey(ClientKey ?? 0), Limit: MaxSuggestions), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Suggestions.Clear();
            foreach (var task in results)
            {
                Suggestions.Add(SuggestionItem.For(task));
            }

            if (!results.Any(r => Text.SameName(r.Title, text)))
            {
                Suggestions.Add(new SuggestionItem(null, $"Create task “{text}”", $"for {NewTaskClientName ?? "a client"}", null));
            }

            SelectedSuggestion = Suggestions.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task search failed");
        }
    }

    /// <summary>Completes when the latest search has finished (tests).</summary>
    internal Task WhenSearchSettledAsync() => _searchTask;

    private static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
}
