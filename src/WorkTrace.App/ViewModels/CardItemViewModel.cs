using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;

namespace WorkTrace.App.ViewModels;

/// <summary>A card on the board, in the Inbox or in the Archive. Updated in place when the card changes.</summary>
public sealed partial class CardItemViewModel(CardSummary summary) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Key), nameof(Title), nameof(Status), nameof(CategoryText), nameof(HasCategory),
        nameof(PriorityText), nameof(IsUrgent), nameof(ShowPriority), nameof(IsBlocked), nameof(BlockedText), nameof(CommentCount),
        nameof(AttachmentCount), nameof(LinkCount), nameof(HasTask), nameof(IsArchived), nameof(Meta), nameof(AutomationName))]
    public partial CardSummary Summary { get; set; } = summary;

    public int Id => Summary.Id;

    public string Key => Summary.Key;

    public string Title => Summary.Title;

    public BoardStatus Status => Summary.Status;

    public bool HasCategory => Summary.Category is not null;

    public string CategoryText => CardText.Category(Summary.Category);

    public string PriorityText => CardText.Priority(Summary.Priority);

    public bool ShowPriority => Summary.Priority != BoardPriority.Normal;

    public bool IsUrgent => Summary.Priority >= BoardPriority.High;

    public bool IsBlocked => Summary.IsBlocked;

    public string BlockedText => "Waiting for " + string.Join(", ", Summary.BlockedBy.Where(b => b.Status != BoardStatus.Done).Select(b => b.Key));

    public int CommentCount => Summary.CommentCount;

    public int AttachmentCount => Summary.AttachmentCount;

    public int LinkCount => Summary.LinkCount;

    public bool HasTask => Summary.WorkTaskId is not null;

    public bool IsArchived => Summary.IsArchived;

    /// <summary>Second line in lists: created date, or archived date for archived cards.</summary>
    public string Meta => Summary.IsArchived && Summary.ArchivedAtUtc is { } archived
        ? $"{BoardRules.StatusName(Summary.Status)} · archived {CardText.Date(archived)}"
        : $"{BoardRules.StatusName(Summary.Status)} · created {CardText.Date(Summary.CreatedAtUtc)}";

    public string AutomationName => $"{Key} {Title}";
}

/// <summary>A Kanban column.</summary>
public sealed partial class BoardColumnViewModel(BoardStatus status) : ObservableObject
{
    public BoardStatus Status { get; } = status;

    public string Title { get; } = BoardRules.StatusName(status);

    public ObservableCollection<CardItemViewModel> Cards { get; } = [];

    [ObservableProperty]
    public partial int Count { get; set; }

    public bool IsDone => Status == BoardStatus.Done;
}

/// <summary>Display texts for card fields.</summary>
public static class CardText
{
    public static string Category(BoardCategory? category) => category switch
    {
        null => "None",
        BoardCategory.Feature => "Feature",
        BoardCategory.Bug => "Bug",
        BoardCategory.Architecture => "Architecture",
        BoardCategory.Testing => "Testing",
        BoardCategory.Idea => "Idea",
        _ => category.ToString()!,
    };

    public static string Priority(BoardPriority priority) => priority switch
    {
        BoardPriority.Low => "Low",
        BoardPriority.Normal => "Normal",
        BoardPriority.High => "High",
        BoardPriority.Critical => "Critical",
        _ => priority.ToString(),
    };

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string DateTime(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    // Picker keys: 0 is reserved for "none"/"all" (PickerOption.NoneKey), so enum values are shifted by one.
    public static IReadOnlyList<PickerOption> CategoryOptions(string noneText) =>
        [new(PickerOption.NoneKey, noneText), .. Enum.GetValues<BoardCategory>().Select(c => new PickerOption((int)c, Category(c)))];

    public static IReadOnlyList<PickerOption> PriorityOptions() =>
        [.. Enum.GetValues<BoardPriority>().Select(p => new PickerOption((int)p + 1, Priority(p)))];

    public static IReadOnlyList<PickerOption> StatusOptions() =>
        [.. Enum.GetValues<BoardStatus>().Select(s => new PickerOption((int)s + 1, BoardRules.StatusName(s)))];

    public static int KeyOf(BoardPriority priority) => (int)priority + 1;

    public static int KeyOf(BoardStatus status) => (int)status + 1;

    public static int KeyOf(BoardCategory? category) => category is { } c ? (int)c : PickerOption.NoneKey;

    public static BoardPriority PriorityFromKey(int key) => (BoardPriority)(key - 1);

    public static BoardStatus StatusFromKey(int key) => (BoardStatus)(key - 1);

    public static BoardCategory? CategoryFromKey(int key) => key == PickerOption.NoneKey ? null : (BoardCategory)key;
}
