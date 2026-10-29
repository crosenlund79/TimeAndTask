using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Board;

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
public sealed partial class BackupViewModel(
    IBackupService backups,
    CardPanelServices services,
    ILogger<BackupViewModel> logger) : ViewModelBase(services.Notifications, logger)
{
    private string? _restorePath;

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
        await RunAsync(async () =>
        {
            var months = await backups.GetMonthsAsync();
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
            foreach (var card in await services.Board.GetCardRefsAsync())
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
        var path = services.Files.SaveFile("Save backup", "WorkTrace backup (*.zip)|*.zip", backups.DefaultFileName(request));
        if (path is null)
        {
            return;
        }

        BackupResult? result = null;
        if (await RunAsync(async () => result = await backups.CreateAsync(request, path), "Could not write the backup."))
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
        var files = services.Files.OpenFiles("Restore from a backup or board.json", "WorkTrace backup or board (*.zip;*.json)|*.zip;*.json", multiselect: false);
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
        if (await RunAsync(async () => preview = await services.Board.PreviewImportAsync(path), "Could not read the backup."))
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
        if (await RunAsync(async () => result = await services.Board.ImportAsync(_restorePath, overwrite), "Could not restore the backup."))
        {
            ClearRestore();
            LastResult = $"Restored: {result!.Added} added, {result.Updated} updated, {result.Unchanged} unchanged, {result.Skipped} kept, "
                + $"{result.AttachmentsAdded} screenshot(s)." + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "");
            Notifications.Show("Restore finished.", NotificationKind.Success);
            services.Session.Announce();
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
