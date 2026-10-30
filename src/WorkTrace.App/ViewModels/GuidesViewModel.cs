using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Common;
using WorkTrace.Core.Guides;

namespace WorkTrace.App.ViewModels;

/// <summary>A file attached to the open guide.</summary>
public sealed record GuideFileViewModel(GuideAttachmentItem Item)
{
    public string Info => (Item.SizeBytes < 1024 * 1024 ? $"{Math.Max(1, Item.SizeBytes / 1024)} KB" : $"{Item.SizeBytes / (1024.0 * 1024):0.0} MB")
        + $" · added {CardText.Date(Item.CreatedAtUtc)}";
}

/// <summary>A row in the guide list, grouped by category.</summary>
public sealed class GuideItemViewModel(GuideSummary summary, DateTime nowUtc)
{
    public GuideSummary Summary { get; } = summary;

    public int Id => Summary.Id;

    public string Title => Summary.Title;

    public string CategoryText => Summary.Category.Length == 0 ? "Uncategorised" : Summary.Category;

    public bool IsStale => nowUtc - Summary.ModifiedAtUtc > GuidesViewModel.StaleAfter;

    public string ModifiedText => $"Modified {CardText.Date(Summary.ModifiedAtUtc)} ({RelativeTime.Format(Summary.ModifiedAtUtc, nowUtc, TimeZoneInfo.Local)})";

    public string CreatedText => $"Added {CardText.Date(Summary.CreatedAtUtc)}";
}

/// <summary>
/// Guides page: searchable list grouped by category on the left, the open guide on the right.
/// Edits save automatically shortly after typing stops.
/// </summary>
public sealed partial class GuidesViewModel(
    IGuideService guides,
    IDialogService dialogs,
    IFileDialogService files,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<GuidesViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel, IDisposable
{
    /// <summary>Guides not modified for this long are marked stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(180);

    public const string AllCategories = "All categories";

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _saveCts;
    private bool _loading;
    private bool _dirty;

    public ObservableCollection<GuideItemViewModel> Items { get; } = [];

    public ObservableCollection<string> CategoryFilterOptions { get; } = [AllCategories];

    public ObservableCollection<string> Categories { get; } = [];

    public ObservableCollection<GuideFileViewModel> Attachments { get; } = [];

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial string? CategoryFilter { get; set; } = AllCategories;

    [ObservableProperty]
    public partial bool OnlyStale { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial GuideItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuide))]
    public partial GuideDetail? Guide { get; private set; }

    public bool HasGuide => Guide is not null;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string? Category { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; } = "";

    [ObservableProperty]
    public partial string? SaveState { get; private set; }

    [ObservableProperty]
    public partial string? Dates { get; private set; }

    public bool HasUnsavedChanges => _dirty;

    public async Task ActivateAsync(object? parameter)
    {
        await SaveNowAsync();
        await ReloadAsync();
    }

    partial void OnSearchTextChanged(string? value) => ScheduleReload();

    partial void OnCategoryFilterChanged(string? value) => ScheduleReload();

    partial void OnOnlyStaleChanged(bool value) => ScheduleReload();

    private void ScheduleReload()
    {
        if (_loading)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = Task.Delay(150, _searchCts.Token).ContinueWith(
            _ => ReloadAsync(), _searchCts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public Task ReloadAsync() => RunAsync(async () =>
    {
        var now = UtcTime.Now(timeProvider);
        var category = CategoryFilter is null or AllCategories ? null : CategoryFilter;
        var list = (await guides.SearchAsync(SearchText, category)).Select(g => new GuideItemViewModel(g, now)).ToList();
        var stale = list.Count(i => i.IsStale);
        if (OnlyStale)
        {
            list = [.. list.Where(i => i.IsStale)];
        }

        var selectedId = SelectedItem?.Id ?? Guide?.Id;
        _loading = true;
        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        SelectedItem = Items.FirstOrDefault(i => i.Id == selectedId);

        // Lists are only rebuilt when they changed, so the editable category box keeps its text.
        var categories = await guides.GetCategoriesAsync();
        var filter = CategoryFilter;
        if (!Categories.SequenceEqual(categories))
        {
            Categories.Clear();
            CategoryFilterOptions.Clear();
            CategoryFilterOptions.Add(AllCategories);
            foreach (var c in categories)
            {
                Categories.Add(c);
                CategoryFilterOptions.Add(c);
            }
        }

        CategoryFilter = filter is not null && CategoryFilterOptions.Contains(filter) ? filter : AllCategories;
        _loading = false;

        Summary = $"{list.Count} guide{(list.Count == 1 ? "" : "s")}" + (stale > 0 ? $" · {stale} not changed for 6+ months" : "");
    }, "Could not load the guides.");

    partial void OnSelectedItemChanged(GuideItemViewModel? value)
    {
        if (!_loading && value is not null && value.Id != Guide?.Id)
        {
            _ = OpenAsync(value.Id);
        }
    }

    private async Task OpenAsync(int id)
    {
        await SaveNowAsync();
        await RunAsync(async () => Show(await guides.GetAsync(id)), "Could not open the guide.");
    }

    private void Show(GuideDetail? guide)
    {
        _loading = true;
        Guide = guide;
        Title = guide?.Title ?? "";
        Category = guide?.Category;
        Content = guide?.Content ?? "";
        Dates = guide is null ? null : $"Added {CardText.DateTime(guide.CreatedAtUtc)} · modified {CardText.DateTime(guide.ModifiedAtUtc)}";
        SaveState = guide is null ? null : "Saved";
        Attachments.Clear();
        foreach (var a in guide?.Attachments ?? [])
        {
            Attachments.Add(new GuideFileViewModel(a));
        }

        _dirty = false;
        _loading = false;
    }

    [RelayCommand]
    private async Task NewGuideAsync()
    {
        await SaveNowAsync();
        var category = CategoryFilter is null or AllCategories ? null : CategoryFilter;
        GuideDetail? created = null;
        await RunAsync(async () => created = await guides.CreateAsync("New guide", category), "Could not create the guide.");
        if (created is not null)
        {
            SearchText = null;
            Show(created);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Guide is not { } guide || !dialogs.Confirm("Delete guide", $"Delete \"{guide.Title}\"? This cannot be undone.", "Delete", destructive: true))
        {
            return;
        }

        _dirty = false;
        if (await RunAsync(() => guides.DeleteAsync(guide.Id), "Could not delete the guide."))
        {
            Show(null);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    // ---- PDF attachments ----

    [RelayCommand]
    private async Task AttachPdfAsync()
    {
        if (Guide is not { } guide)
        {
            return;
        }

        foreach (var path in files.OpenFiles("Attach PDF documents", "PDF documents (*.pdf)|*.pdf|All files|*.*"))
        {
            if (await RunAsync(async () => Attachments.Add(new GuideFileViewModel(await guides.AddAttachmentAsync(guide.Id, path))), "Could not attach the file."))
            {
                Notifications.Show($"Attached {System.IO.Path.GetFileName(path)}.", NotificationKind.Success);
            }
        }
    }

    [RelayCommand]
    private void OpenAttachment(GuideFileViewModel file)
    {
        if (!System.IO.File.Exists(file.Item.FullPath))
        {
            Notifications.Show($"{file.Item.FileName} is missing from the data folder.", NotificationKind.Warning);
            return;
        }

        files.OpenFile(file.Item.FullPath);
    }

    [RelayCommand]
    private async Task RemoveAttachmentAsync(GuideFileViewModel file)
    {
        if (dialogs.Confirm("Remove attachment", $"Remove {file.Item.FileName} from this guide? The copy in WorkTrace's data folder is deleted.", "Remove", destructive: true)
            && await RunAsync(() => guides.RemoveAttachmentAsync(file.Item.Id), "Could not remove the attachment."))
        {
            Attachments.Remove(file);
        }
    }

    // ---- Auto-save ----

    partial void OnTitleChanged(string value) => Changed();

    partial void OnCategoryChanged(string? value) => Changed();

    partial void OnContentChanged(string value) => Changed();

    private void Changed()
    {
        if (_loading || Guide is null)
        {
            return;
        }

        _dirty = true;
        SaveState = "Saving…";
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        _ = Task.Delay(700, _saveCts.Token).ContinueWith(
            _ => SaveNowAsync(), _saveCts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Saves pending edits now (before switching guides or pages).</summary>
    public async Task SaveNowAsync()
    {
        if (!_dirty || Guide is not { } guide)
        {
            return;
        }

        _dirty = false;
        _saveCts?.Cancel();
        GuideDetail? saved = null;
        if (await RunAsync(async () => saved = await guides.UpdateAsync(guide.Id, Title, Category, Content), "Could not save the guide."))
        {
            if (Guide?.Id == saved!.Id)
            {
                Guide = saved;
                Dates = $"Added {CardText.DateTime(saved.CreatedAtUtc)} · modified {CardText.DateTime(saved.ModifiedAtUtc)}";
            }

            SaveState = "Saved";
            await ReloadAsync();
        }
        else
        {
            SaveState = "Not saved";
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _saveCts?.Cancel();
        _saveCts?.Dispose();
    }
}
