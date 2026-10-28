using WorkTrace.Core.Validation;

namespace WorkTrace.Core.Management;

/// <summary>
/// Deterministic in-memory ranking for task search and autocomplete.
/// <para>
/// Every whitespace-separated term must match at least one field (external ID, title, client,
/// task system); matching ignores case and accents. Matches on the external ID rank highest
/// (people often type the ticket number), then title, client and system. Ties are broken by
/// recency of use, then title, then id. An empty query returns tasks by recency.
/// </para>
/// </summary>
public static class TaskSearch
{
    public static IReadOnlyList<TaskSummary> Rank(IEnumerable<TaskSummary> tasks, string? query, DateTime nowUtc, int limit)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var terms = Tokenize(query);

        IEnumerable<(TaskSummary Task, int Score)> scored = terms.Length == 0
            ? tasks.Select(t => (t, RecencyBoost(t, nowUtc)))
            : tasks.Select(t => (t, Score(t, terms, nowUtc))).Where(x => x.Item2 > 0);

        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Task.LastUsedAtUtc ?? DateTime.MinValue)
            .ThenByDescending(x => x.Task.ModifiedAtUtc)
            .ThenBy(x => x.Task.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Task.Id)
            .Take(Math.Max(0, limit))
            .Select(x => x.Task)
            .ToList();
    }

    /// <summary>Returns 0 when the task does not match all terms; otherwise a positive relevance score.</summary>
    public static int Score(TaskSummary task, IReadOnlyList<string> terms, DateTime nowUtc)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var best = Math.Max(
                Math.Max(ExternalIdScore(task.ExternalId, term), FieldScore(task.Title, term, prefix: 300, wordStart: 200, contains: 100)),
                Math.Max(
                    FieldScore(task.ClientName, term, prefix: 150, wordStart: 120, contains: 60),
                    FieldScore(task.TaskSystemName, term, prefix: 80, wordStart: 60, contains: 30)));

            if (best == 0)
            {
                return 0;
            }

            total += best;
        }

        return total + RecencyBoost(task, nowUtc) - (task.IsArchived ? 50 : 0);
    }

    internal static string[] Tokenize(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ExternalIdScore(string? externalId, string term)
    {
        if (string.IsNullOrEmpty(externalId))
        {
            return 0;
        }

        if (Text.SameName(externalId, term))
        {
            return 1000;
        }

        // "42" should find "00042" and "BUG-42": compare without leading zeros and separators too.
        var compactId = Compact(externalId);
        var compactTerm = Compact(term);
        if (compactTerm.Length > 0 && string.Equals(compactId, compactTerm, StringComparison.OrdinalIgnoreCase))
        {
            return 900;
        }

        if (Text.StartsWith(externalId, term))
        {
            return 600;
        }

        return Text.IndexOf(externalId, term) >= 0 ? 400 : 0;
    }

    private static int FieldScore(string? field, string term, int prefix, int wordStart, int contains)
    {
        var index = Text.IndexOf(field, term);
        if (index < 0)
        {
            return 0;
        }

        if (index == 0)
        {
            return prefix;
        }

        // Word start: an occurrence preceded by a non-letter/digit (space, dash, slash, ...).
        for (var i = index; i >= 0;)
        {
            if (i > 0 && !char.IsLetterOrDigit(field![i - 1]))
            {
                return wordStart;
            }

            var next = Text.IndexOf(field![(i + 1)..], term);
            i = next < 0 ? -1 : i + 1 + next;
        }

        return contains;
    }

    private static int RecencyBoost(TaskSummary task, DateTime nowUtc)
    {
        if (task.LastUsedAtUtc is not { } used)
        {
            return 0;
        }

        var age = nowUtc - used;
        return age <= TimeSpan.FromDays(1) ? 40
            : age <= TimeSpan.FromDays(7) ? 25
            : age <= TimeSpan.FromDays(30) ? 10
            : 0;
    }

    /// <summary>Upper-cased letters/digits only, with leading zeros of the digit run removed ("BUG-0042" → "BUG42").</summary>
    private static string Compact(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        var s = new string(chars);
        var firstDigit = Array.FindIndex(chars, char.IsDigit);
        if (firstDigit >= 0)
        {
            var end = firstDigit;
            while (end < s.Length - 1 && s[end] == '0' && char.IsDigit(s[end + 1]))
            {
                end++;
            }

            s = s[..firstDigit] + s[end..];
        }

        return s.ToUpperInvariant();
    }
}
