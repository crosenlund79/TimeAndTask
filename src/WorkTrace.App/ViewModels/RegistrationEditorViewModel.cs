using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkTrace.Core.Management;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// Edits one registration (or classifies an unclassified interval): stakeholder, task, date, start, end and duration.
/// Saving replaces the original range; nothing is rounded and the history keeps the earlier values.
/// </summary>
public sealed partial class RegistrationEditorViewModel(ITaskService tasks, IClientService clients, IRegistrationService registrations) : ObservableObject
{
    private RegistrationEntry? _original;
    private IReadOnlyList<TaskSummary> _allTasks = [];
    private bool _syncing;

    /// <summary>Raised when the dialog should close (true = saved).</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Heading { get; private set; } = "Edit registration";

    public ObservableCollection<PickerOption> ClientOptions { get; } = [];

    /// <summary>Tasks of the chosen stakeholder (all when none is chosen), plus "No task (stakeholder only)".</summary>
    public ObservableCollection<PickerOption> TaskOptions { get; } = [];

    public IReadOnlyList<string> QuarterHours { get; } =
        [.. Enumerable.Range(0, 96).Select(i => new TimeOnly(i / 4, i % 4 * 15).ToString("HH:mm", CultureInfo.InvariantCulture))];

    [ObservableProperty]
    public partial int? ClientKey { get; set; } = PickerOption.NoneKey;

    [ObservableProperty]
    public partial int? TaskKey { get; set; } = PickerOption.NoneKey;

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    public partial string? StartText { get; set; }

    [ObservableProperty]
    public partial string? EndText { get; set; }

    [ObservableProperty]
    public partial string? DurationText { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public async Task LoadAsync(RegistrationEntry entry)
    {
        _original = entry;
        Heading = entry.Kind == RegistrationKind.Unclassified ? "Classify time" : "Edit registration";
        OnPropertyChanged(nameof(Heading));
        _allTasks = await tasks.SearchAsync(new TaskQuery(Limit: 500));
        ClientOptions.Clear();
        ClientOptions.Add(new PickerOption(PickerOption.NoneKey, "Choose a stakeholder…"));
        foreach (var c in await clients.GetClientsForPickerAsync())
        {
            ClientOptions.Add(new PickerOption(c.Id, c.Name, c.IsActive));
        }

        var start = TimeZoneInfo.ConvertTimeFromUtc(entry.StartUtc, TimeZoneInfo.Local);
        var end = TimeZoneInfo.ConvertTimeFromUtc(entry.EndUtc, TimeZoneInfo.Local);
        _syncing = true;
        ClientKey = entry.ClientId ?? PickerOption.NoneKey;
        RebuildTasks();
        TaskKey = entry.TaskId ?? PickerOption.NoneKey;
        Date = start.Date;
        StartText = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndText = end.ToString("HH:mm", CultureInfo.InvariantCulture);
        DurationText = Format(end - start);
        _syncing = false;
    }

    partial void OnClientKeyChanged(int? value)
    {
        if (_syncing)
        {
            return;
        }

        var keep = TaskKey;
        RebuildTasks();
        TaskKey = TaskOptions.Any(o => o.Id == keep) ? keep : PickerOption.NoneKey;
    }

    partial void OnTaskKeyChanged(int? value)
    {
        // Choosing a task sets its stakeholder.
        if (!_syncing && PickerOption.FromKey(value ?? 0) is { } id && _allTasks.FirstOrDefault(t => t.Id == id) is { } task && ClientKey != task.ClientId)
        {
            _syncing = true;
            ClientKey = task.ClientId;
            RebuildTasks();
            TaskKey = id;
            _syncing = false;
        }
    }

    // Start or end changed: show the new duration. Duration changed: move the end.
    partial void OnStartTextChanged(string? value) => UpdateDuration();

    partial void OnEndTextChanged(string? value) => UpdateDuration();

    partial void OnDurationTextChanged(string? value)
    {
        if (_syncing || !TryTime(StartText, out var start) || Hours.ParseMinutes(value) is not { } minutes || minutes <= 0)
        {
            return;
        }

        _syncing = true;
        EndText = start.AddMinutes(minutes).ToString("HH:mm", CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void UpdateDuration()
    {
        if (_syncing || !TryTime(StartText, out var start) || !TryTime(EndText, out var end))
        {
            return;
        }

        _syncing = true;
        DurationText = Format(end > start ? end - start : end - start + TimeSpan.FromDays(1));
        _syncing = false;
    }

    private void RebuildTasks()
    {
        var clientId = PickerOption.FromKey(ClientKey ?? 0);
        TaskOptions.Clear();
        TaskOptions.Add(new PickerOption(PickerOption.NoneKey, "No task (stakeholder only)"));
        foreach (var t in _allTasks.Where(t => !t.IsClosed && (clientId is null || t.ClientId == clientId)).OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            TaskOptions.Add(new PickerOption(t.Id, clientId is null ? $"{t.Title} ({t.ClientName})" : t.Title));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = null;
        if (_original is not { } original)
        {
            return;
        }

        if (Date is not { } date || !TryTime(StartText, out var startTime) || !TryTime(EndText, out var endTime))
        {
            Error = "Enter a date and times such as 08:00 and 09:15.";
            return;
        }

        var day = DateOnly.FromDateTime(date);
        var startLocal = day.ToDateTime(startTime);
        var endLocal = (endTime > startTime ? day : day.AddDays(1)).ToDateTime(endTime); // an end before the start is the next morning
        var startUtc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(startLocal, TimeZoneInfo.Local), DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(endLocal, TimeZoneInfo.Local), DateTimeKind.Utc);
        var taskId = PickerOption.FromKey(TaskKey ?? 0);
        var clientId = PickerOption.FromKey(ClientKey ?? 0);
        try
        {
            if (original.Kind == RegistrationKind.Work)
            {
                await registrations.EditAsync(original.StartUtc, original.EndUtc, startUtc, endUtc, taskId, clientId);
            }
            else
            {
                await registrations.AssignAsync(startUtc, endUtc, taskId, clientId);
            }

            CloseRequested?.Invoke(this, true);
        }
        catch (DomainValidationException ex)
        {
            Error = ex.Errors.ByField.SelectMany(e => e.Value).FirstOrDefault() ?? ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm", "H.mm", "HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string Format(TimeSpan span) => $"{(int)span.TotalHours}:{span.Minutes:00}";
}
