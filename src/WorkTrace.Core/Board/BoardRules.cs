using System.Globalization;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;

namespace WorkTrace.Core.Board;

/// <summary>Pure board rules shared by the service, the import and the UI.</summary>
public static class BoardRules
{
    /// <summary>Gap between neighbouring cards when a column is (re)numbered.</summary>
    public const double SortStep = 1024;

    public const string OwnerAuthor = "owner";

    public static IReadOnlyList<BoardStatus> Columns { get; } =
        [BoardStatus.Backlog, BoardStatus.Todo, BoardStatus.Doing, BoardStatus.Review, BoardStatus.Done];

    public static string StatusName(BoardStatus status) => status switch
    {
        BoardStatus.Inbox => "Inbox",
        BoardStatus.Backlog => "Backlog",
        BoardStatus.Todo => "To do",
        BoardStatus.Doing => "In progress",
        BoardStatus.Review => "Review / Test",
        BoardStatus.Done => "Done",
        _ => status.ToString(),
    };

    /// <summary>Only the owner moves user-facing work (features, bugs, uncategorised) to Done.</summary>
    public static bool NeedsOwnerAcceptance(BoardCategory? category) =>
        category is null or BoardCategory.Feature or BoardCategory.Bug;

    /// <summary>Parses "WT-042", "wt-42" or "42".</summary>
    public static int? ParseNumber(string? key)
    {
        var text = Text.Clean(key);
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith(BoardCard.KeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[BoardCard.KeyPrefix.Length..];
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static ValidationErrors Validate(CardFields fields)
    {
        var errors = new ValidationErrors();
        var title = Text.Clean(fields.Title);
        if (title is null)
        {
            errors.Add(nameof(CardFields.Title), "Enter a title.");
        }
        else if (title.Length > BoardCard.TitleMaxLength)
        {
            errors.Add(nameof(CardFields.Title), $"Use at most {BoardCard.TitleMaxLength} characters.");
        }

        foreach (var (name, value) in new[]
                 {
                     (nameof(CardFields.Description), fields.Description), (nameof(CardFields.Acceptance), fields.Acceptance),
                     (nameof(CardFields.Notes), fields.Notes), (nameof(CardFields.Evidence), fields.Evidence),
                 })
        {
            if (value?.Length > BoardCard.TextMaxLength)
            {
                errors.Add(name, $"Use at most {BoardCard.TextMaxLength:N0} characters.");
            }
        }

        return errors;
    }

    /// <summary>Cleans links; a missing label becomes the URL's host and path.</summary>
    public static IReadOnlyList<LinkInput> CleanLinks(IReadOnlyList<LinkInput> links)
    {
        var errors = new ValidationErrors();
        var result = new List<LinkInput>();
        foreach (var link in links)
        {
            var url = Text.Clean(link.Url);
            if (url is null && Text.Clean(link.Label) is null)
            {
                continue;
            }

            if (!IsWebUrl(url) || url!.Length > BoardLink.UrlMaxLength)
            {
                errors.Add("Links", $"\"{url}\" is not a web address (http:// or https://).");
                continue;
            }

            var label = Text.Clean(link.Label) ?? DefaultLabel(url);
            result.Add(new LinkInput(label.Length > BoardLink.LabelMaxLength ? label[..BoardLink.LabelMaxLength] : label, url));
        }

        errors.ThrowIfInvalid();
        return result;
    }

    public static string DefaultLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var text = uri.Host + uri.AbsolutePath.TrimEnd('/');
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    /// <summary>
    /// True when making <paramref name="cardId"/> wait for <paramref name="blockedByIds"/> would create a cycle.
    /// <paramref name="edges"/> holds the existing "card → cards it waits for" relation.
    /// </summary>
    public static bool CreatesCycle(int cardId, IEnumerable<int> blockedByIds, IReadOnlyDictionary<int, IReadOnlyList<int>> edges)
    {
        var stack = new Stack<int>(blockedByIds);
        var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == cardId)
            {
                return true;
            }

            if (!seen.Add(current) || !edges.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var n in next)
            {
                stack.Push(n);
            }
        }

        return false;
    }

    /// <summary>
    /// Sort value for a card placed between <paramref name="previous"/> and <paramref name="next"/> (either may be null).
    /// Returns null when the gap is exhausted and the column must be renumbered first.
    /// </summary>
    public static double? SortBetween(double? previous, double? next)
    {
        var value = (previous, next) switch
        {
            (null, null) => SortStep,
            (null, { } n) => n - SortStep,
            ({ } p, null) => p + SortStep,
            ({ } p, { } n) => p + ((n - p) / 2),
        };
        return (previous is { } a && value <= a) || (next is { } b && value >= b) ? null : value;
    }

    /// <summary>Accent- and case-insensitive search across all words in <paramref name="text"/>.</summary>
    public static bool Matches(string? text, IEnumerable<string?> haystack)
    {
        var words = Text.Clean(text)?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is null || words.Length == 0)
        {
            return true;
        }

        var fields = haystack.Where(h => !string.IsNullOrEmpty(h)).ToList();
        return words.All(w => fields.Any(f => Text.IndexOf(f, w) >= 0));
    }

    /// <summary>Web-board rank for the n-th card of a column: fixed width, lexicographic, never ending in '0'.</summary>
    public static string RankFor(int index)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var n = index + 1;
        var chars = new char[5];
        for (var i = 4; i >= 0; i--)
        {
            chars[i] = digits[n % 36];
            n /= 36;
        }

        return new string(chars) + "i";
    }
}

/// <summary>Per-session undo history (most recent first), capped.</summary>
public sealed class BoardUndoHistory
{
    public const int Capacity = 100;

    private readonly LinkedList<CardEdit> _edits = new();

    /// <summary>Description of field edits; consecutive ones on the same card undo as one step (auto-save while typing).</summary>
    public const string FieldsEdit = "Edit";

    public bool CanUndo => _edits.Count > 0;

    public CardEdit? Peek => _edits.First?.Value;

    public void Push(CardEdit? edit)
    {
        if (edit is null)
        {
            return;
        }

        if (edit.Description == FieldsEdit && _edits.First?.Value is { } top && top.CardId == edit.CardId
            && top.Description == FieldsEdit && top.After.SameAs(edit.Before))
        {
            _edits.First.Value = top with { After = edit.After };
            return;
        }

        _edits.AddFirst(edit);
        if (_edits.Count > Capacity)
        {
            _edits.RemoveLast();
        }
    }

    public CardEdit? Pop()
    {
        var first = _edits.First;
        if (first is null)
        {
            return null;
        }

        _edits.RemoveFirst();
        return first.Value;
    }

    public void Clear() => _edits.Clear();
}
