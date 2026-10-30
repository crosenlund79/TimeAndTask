using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Board;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.ViewModels;

public sealed record MonthOptionViewModel(BackupMonth Month)
{
    public string Text => $"{Month.Month} · {Month.CardCount} cards, {Month.AttachmentCount} screenshots";

    public string State => Month.LastBackupAtUtc is { } last
        ? Month.ChangedSinceBackup ? $"changed since backup {CardText.DateTime(last)}" : $"backed up {CardText.DateTime(last)}"
        : "never backed up";

    public bool Changed => Month.ChangedSinceBackup;
}

public sealed partial class SelectableCardViewModel(CardRef card) : ObservableObject
{
    public CardRef Card { get; } = card;

    public string Text => $"{Card.Key} {Card.Title}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>One line of a restore preview. Cards edited here after the backup are kept unless ticked.</summary>
public sealed partial class RestoreItemViewModel(ImportItem item) : ObservableObject
{
    public ImportItem Item { get; } = item;

    public string Key => Item.NewKey is { } newKey && Item.Kind == ImportItemKind.NumberTaken ? $"{Item.Key} → {newKey}" : Item.Key;

    public string Title => Item.Title;

    public string KindText => Item.Kind switch
    {
        ImportItemKind.New => "New",
        ImportItemKind.Same => "Unchanged",
        ImportItemKind.Changed => "Will be updated",
        ImportItemKind.LocalNewer => "Edited here since – kept",
        ImportItemKind.NumberTaken => "New, gets a new number",
        _ => Item.Kind.ToString(),
    } + (Item.NewComments > 0 && Item.Kind != ImportItemKind.New ? $" · {Item.NewComments} new comment(s)" : "")
      + (Item.NewAttachments > 0 ? $" · {Item.NewAttachments} screenshot(s)" : "");

    public bool CanOverwrite => Item.Kind == ImportItemKind.LocalNewer;

    [ObservableProperty]
    public partial bool Overwrite { get; set; }
}

/// <summary>
/// Backup & restore panel (on the Board page): complete, all-card, monthly or selected-card ZIPs, and restore with a
/// preview. Email delivery is prepared (per-month change tracking) but not implemented until a method is approved.
/// </summary>
public sealed partial class BackupViewModel : ViewModelBase
{
    private readonly IBackupService _backups;
    private readonly CardPanelServices _services;
    private readonly ISettingsStore _settings;
    private readonly IAutomaticBackupService _automatic;
    private readonly ISecretStore _secrets;
    private string? _restorePath;
    private bool _loading;

    public BackupViewModel(
        IBackupService backups,
        CardPanelServices services,
        ISettingsStore settings,
        IAutomaticBackupService automatic,
        ISecretStore secrets,
        ILogger<BackupViewModel> logger)
        : base(services.Notifications, logger)
    {
        _backups = backups;
        _services = services;
        _settings = settings;
        _automatic = automatic;
        _secrets = secrets;
        settings.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(LoadAutomatic);
    }

    // ---- Automatic backups (settings saved at once) ----

    public IReadOnlyList<PickerOption> FrequencyOptions { get; } =
        [new((int)BackupFrequency.Daily + 1, "Daily"), new((int)BackupFrequency.Weekly + 1, "Weekly")];

    [ObservableProperty]
    public partial int? FrequencyKey { get; set; }

    [ObservableProperty]
    public partial bool FolderEnabled { get; set; }

    [ObservableProperty]
    public partial string? Folder { get; private set; }

    [ObservableProperty]
    public partial bool FolderIncludeScreenshots { get; set; }

    [ObservableProperty]
    public partial string? FolderStatus { get; private set; }

    [ObservableProperty]
    public partial string? FolderError { get; private set; }

    [ObservableProperty]
    public partial bool EmailEnabled { get; set; }

    [ObservableProperty]
    public partial string? EmailTo { get; set; }

    [ObservableProperty]
    public partial string? EmailStatus { get; private set; }

    [ObservableProperty]
    public partial string? EmailError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmailKeyState))]
    public partial bool HasEmailKey { get; private set; }

    public string EmailKeyState => HasEmailKey ? "A key is stored in Windows Credential Manager." : "No key stored yet.";

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>Reads the automatic-backup settings and status (also when the backup job updates them).</summary>
    public void LoadAutomatic()
    {
        _loading = true;
        var b = _settings.Current.Backup;
        FrequencyKey = (int)b.Frequency + 1;
        FolderEnabled = b.FolderEnabled;
        Folder = b.Folder;
        FolderIncludeScreenshots = b.FolderIncludeScreenshots;
        FolderStatus = b.LastFolderUtc is { } f ? $"Last backup {CardText.DateTime(f)} · {b.LastFolderResult}" : "No backup to the folder yet.";
        FolderError = b.LastFolderError;
        EmailEnabled = b.EmailEnabled;
        EmailTo = b.EmailTo;
        EmailStatus = b.LastEmailUtc is { } e ? $"Last sent {CardText.DateTime(e)} · {b.LastEmailResult}" : "No backup emailed yet.";
        EmailError = b.LastEmailError;
        try
        {
            HasEmailKey = _secrets.Read(IAutomaticBackupService.EmailKeySecret) is not null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read the email key from Credential Manager");
            HasEmailKey = false;
        }

        _loading = false;
    }

    partial void OnFrequencyKeyChanged(int? value)
    {
        if (value is { } key)
        {
            Save(b => b.Frequency = (BackupFrequency)(key - 1));
        }
    }

    partial void OnFolderEnabledChanged(bool value) => Save(b => b.FolderEnabled = value);

    partial void OnFolderIncludeScreenshotsChanged(bool value) => Save(b => b.FolderIncludeScreenshots = value);

    partial void OnEmailEnabledChanged(bool value) => Save(b => b.EmailEnabled = value);

    partial void OnEmailToChanged(string? value) => Save(b => b.EmailTo = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    private void Save(Action<BackupSettings> change)
    {
        if (!_loading)
        {
            _ = RunAsync(() => _settings.UpdateAsync(s => change(s.Backup)), "Could not save the backup setting.");
        }
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        if (_services.Files.ChooseFolder("Choose the backup folder (local, network or e.g. OneDrive)", Folder) is { } folder)
        {
            Folder = folder;
            Save(b =>
            {
                b.Folder = folder;
                b.FolderEnabled = true;
            });
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Folder is { } folder && System.IO.Directory.Exists(folder))
        {
            _services.Files.OpenFile(folder);
        }
    }

    [RelayCommand]
    private Task BackupNowAsync() => RunNowAsync(BackupDestination.Folder);

    [RelayCommand]
    private Task SendNowAsync() => RunNowAsync(BackupDestination.Email);

    private async Task RunNowAsync(BackupDestination destination)
    {
        IsRunning = true;
        try
        {
            BackupRunResult? result = null;
            await RunAsync(async () => result = await Task.Run(() => _automatic.RunNowAsync(destination)), "The backup could not run.");
            if (result is not null)
            {
                Notifications.Show(result.Message, result.Succeeded ? NotificationKind.Success : NotificationKind.Error);
            }
        }
        finally
        {
            IsRunning = false;
            LoadAutomatic();
            await LoadAsync();
        }
    }

    /// <summary>Stores the email API key in Windows Credential Manager (called by the view with the password box text).</summary>
    public void SaveEmailKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        try
        {
            _secrets.Write(IAutomaticBackupService.EmailKeySecret, key.Trim());
            Notifications.Show("Email key saved in Windows Credential Manager.", NotificationKind.Success);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Saving the email key failed");
            Notifications.Show("The key could not be saved: " + ex.Message, NotificationKind.Error);
        }

        LoadAutomatic();
    }

    [RelayCommand]
    private void RemoveEmailKey()
    {
        if (_services.Dialogs.Confirm("Remove email key", "Remove the stored email key? Email backups stop until you add a key again.", "Remove", destructive: true))
        {
            _secrets.Write(IAutomaticBackupService.EmailKeySecret, null);
            LoadAutomatic();
        }
    }

    public ObservableCollection<MonthOptionViewModel> Months { get; } = [];

    public ObservableCollection<SelectableCardViewModel> Cards { get; } = [];

    public ObservableCollection<RestoreItemViewModel> RestoreItems { get; } = [];

    public ObservableCollection<string> RestoreProblems { get; } = [];

    [ObservableProperty]
    public partial MonthOptionViewModel? SelectedMonth { get; set; }

    [ObservableProperty]
    public partial string? ChangedSummary { get; private set; }

    [ObservableProperty]
    public partial string? LastResult { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestorePreview))]
    public partial ImportPreview? RestorePreview { get; private set; }

    public bool HasRestorePreview => RestorePreview is not null;

    [ObservableProperty]
    public partial string? RestoreSummary { get; private set; }

    public async Task LoadAsync()
    {
        LoadAutomatic();
        await RunAsync(async () =>
        {
            var months = await _backups.GetMonthsAsync();
            Months.Clear();
            foreach (var month in months)
            {
                Months.Add(new MonthOptionViewModel(month));
            }

            SelectedMonth = Months.FirstOrDefault(m => m.Changed) ?? Months.FirstOrDefault();
            var changed = months.Count(m => m.ChangedSinceBackup);
            ChangedSummary = months.Count == 0 ? "No cards yet."
                : changed == 0 ? "Every month is backed up."
                : $"{changed} of {months.Count} month(s) changed since their last backup.";

            Cards.Clear();
            foreach (var card in await _services.Board.GetCardRefsAsync())
            {
                Cards.Add(new SelectableCardViewModel(card));
            }
        }, "Could not load the backup overview.");
    }

    [RelayCommand]
    private Task BackupCompleteAsync() => BackupAsync(new BackupRequest(BackupKind.Complete));

    [RelayCommand]
    private Task BackupAllCardsAsync() => BackupAsync(new BackupRequest(BackupKind.AllCards));

    [RelayCommand]
    private Task BackupMonthAsync() =>
        SelectedMonth is { } month ? BackupAsync(new BackupRequest(BackupKind.Month, month.Month.Month)) : Task.CompletedTask;

    [RelayCommand]
    private Task BackupSelectedAsync()
    {
        var ids = Cards.Where(c => c.IsSelected).Select(c => c.Card.Id).ToList();
        if (ids.Count == 0)
        {
            Notifications.Show("Tick the cards to export first.", NotificationKind.Warning);
            return Task.CompletedTask;
        }

        return BackupAsync(new BackupRequest(BackupKind.Cards, CardIds: ids));
    }

    private async Task BackupAsync(BackupRequest request)
    {
        var path = _services.Files.SaveFile("Save backup", "WorkTrace backup (*.zip)|*.zip", _backups.DefaultFileName(request));
        if (path is null)
        {
            return;
        }

        BackupResult? result = null;
        if (await RunAsync(async () => result = await _backups.CreateAsync(request, path), "Could not write the backup."))
        {
            LastResult = $"Saved {Path.GetFileName(result!.Path)}: {result.CardCount} cards, {result.AttachmentCount} screenshots"
                + (result.IncludesDatabase ? ", database and settings" : "") + $", {result.SizeBytes / 1024.0:N0} KB.";
            Notifications.Show("Backup saved.", NotificationKind.Success);
            await LoadAsync();
        }
    }

    // ---- Restore ----

    [RelayCommand]
    private async Task ChooseRestoreFileAsync()
    {
        var files = _services.Files.OpenFiles("Restore from a backup or board.json", "WorkTrace backup or board (*.zip;*.json)|*.zip;*.json", multiselect: false);
        if (files.Count > 0)
        {
            await PreviewRestoreAsync(files[0]);
        }
    }

    /// <summary>Checks the file and shows what would change; nothing is written yet.</summary>
    public async Task PreviewRestoreAsync(string path)
    {
        ClearRestore();
        ImportPreview? preview = null;
        if (await RunAsync(async () => preview = await _services.Board.PreviewImportAsync(path), "Could not read the backup."))
        {
            _restorePath = path;
            RestorePreview = preview;
            foreach (var item in preview!.Items.OrderBy(i => i.Kind == ImportItemKind.Same).ThenBy(i => BoardRules.ParseNumber(i.Key)))
            {
                RestoreItems.Add(new RestoreItemViewModel(item));
            }

            foreach (var problem in preview.Problems)
            {
                RestoreProblems.Add(problem);
            }

            RestoreSummary = $"{Path.GetFileName(path)}: {preview.Count(ImportItemKind.New) + preview.Count(ImportItemKind.NumberTaken)} new, "
                + $"{preview.Count(ImportItemKind.Changed)} to update, {preview.Count(ImportItemKind.LocalNewer)} edited here since (kept unless ticked), "
                + $"{preview.Count(ImportItemKind.Same)} unchanged, {preview.AttachmentCount} screenshot(s)."
                + (preview.IncludesDatabase ? " The database snapshot in this backup is not restored here (time tracking stays as it is)." : "");
        }
    }

    [RelayCommand]
    private async Task ApplyRestoreAsync()
    {
        if (_restorePath is null)
        {
            return;
        }

        var overwrite = RestoreItems.Where(i => i.Overwrite).Select(i => i.Item.Uid).ToList();
        ImportResult? result = null;
        if (await RunAsync(async () => result = await _services.Board.ImportAsync(_restorePath, overwrite), "Could not restore the backup."))
        {
            ClearRestore();
            LastResult = $"Restored: {result!.Added} added, {result.Updated} updated, {result.Unchanged} unchanged, {result.Skipped} kept, "
                + $"{result.AttachmentsAdded} screenshot(s)." + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "");
            Notifications.Show("Restore finished.", NotificationKind.Success);
            _services.Session.Announce();
            await LoadAsync();
        }
    }

    [RelayCommand]
    private void CancelRestore() => ClearRestore();

    private void ClearRestore()
    {
        _restorePath = null;
        RestorePreview = null;
        RestoreSummary = null;
        RestoreItems.Clear();
        RestoreProblems.Clear();
    }
}
