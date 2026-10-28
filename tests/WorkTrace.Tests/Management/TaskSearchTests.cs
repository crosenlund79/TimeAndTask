using WorkTrace.Core.Management;

namespace WorkTrace.Tests.Management;

public class TaskSearchTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static TaskSummary Task(int id, string title, string? externalId = null, string client = "Contoso", string? system = null, DateTime? lastUsed = null, bool archived = false) =>
        new(id, 1, client, true, null, system, externalId, title, null, null, null, archived, Now.AddDays(-100), Now.AddDays(-100), lastUsed);

    private static int[] Ids(IEnumerable<TaskSummary> tasks) => [.. tasks.Select(t => t.Id)];

    [Fact]
    public void EmptyQuery_ReturnsMostRecentlyUsedFirst()
    {
        var tasks = new[]
        {
            Task(1, "Old", lastUsed: Now.AddDays(-40)),
            Task(2, "Never used"),
            Task(3, "Today", lastUsed: Now.AddHours(-1)),
            Task(4, "Last week", lastUsed: Now.AddDays(-5)),
        };

        Assert.Equal([3, 4, 1, 2], Ids(TaskSearch.Rank(tasks, "  ", Now, 10)));
    }

    [Fact]
    public void AllTermsMustMatch_AcrossFields()
    {
        var tasks = new[]
        {
            Task(1, "Invoice posting error", client: "Contoso"),
            Task(2, "Invoice layout", client: "Fabrikam"),
            Task(3, "Posting profile", client: "Fabrikam"),
        };

        Assert.Equal([2], Ids(TaskSearch.Rank(tasks, "invoice fabrikam", Now, 10)));
    }

    [Fact]
    public void ExternalIdMatch_OutranksTitleMatch()
    {
        var tasks = new[]
        {
            Task(1, "Ticket 4711 follow-up"),
            Task(2, "Batch job hangs", externalId: "4711"),
        };

        Assert.Equal([2, 1], Ids(TaskSearch.Rank(tasks, "4711", Now, 10)));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("0042")]
    [InlineData("bug42")]
    [InlineData("BUG-0042")]
    public void ExternalId_MatchesIgnoringLeadingZerosAndSeparators(string query)
    {
        var tasks = new[] { Task(1, "Crash on save", externalId: "BUG-0042"), Task(2, "Unrelated") };
        Assert.Equal(1, TaskSearch.Rank(tasks, query, Now, 10)[0].Id);
    }

    [Fact]
    public void ExternalId_LeadingZerosAreSignificantForExactMatch()
    {
        var tasks = new[] { Task(1, "A", externalId: "42"), Task(2, "B", externalId: "0042") };
        Assert.Equal(2, TaskSearch.Rank(tasks, "0042", Now, 10)[0].Id);
    }

    [Fact]
    public void Matching_IgnoresCaseAndAccents_IncludingDanishLetters()
    {
        var tasks = new[]
        {
            Task(1, "Møde med økonomi", client: "Ærø Kommune"),
            Task(2, "Café opsætning"),
        };

        Assert.Equal([1], Ids(TaskSearch.Rank(tasks, "MØDE ærø", Now, 10)));
        Assert.Equal([2], Ids(TaskSearch.Rank(tasks, "cafe", Now, 10)));
    }

    [Fact]
    public void TitlePrefix_OutranksWordStart_OutranksContains()
    {
        var tasks = new[]
        {
            Task(1, "Reposting journals"),
            Task(2, "Fix posting"),
            Task(3, "Posting profile"),
        };

        Assert.Equal([3, 2, 1], Ids(TaskSearch.Rank(tasks, "posting", Now, 10)));
    }

    [Fact]
    public void Recency_BreaksEqualMatches()
    {
        var tasks = new[]
        {
            Task(1, "Support call", lastUsed: Now.AddDays(-20)),
            Task(2, "Support mail", lastUsed: Now.AddHours(-2)),
        };

        Assert.Equal([2, 1], Ids(TaskSearch.Rank(tasks, "support", Now, 10)));
    }

    [Fact]
    public void ArchivedTasks_RankBelowActiveEquivalents()
    {
        var tasks = new[] { Task(1, "Deploy", archived: true), Task(2, "Deploy") };
        Assert.Equal([2, 1], Ids(TaskSearch.Rank(tasks, "deploy", Now, 10)));
    }

    [Fact]
    public void Ranking_IsDeterministic_AndRespectsLimit()
    {
        var tasks = Enumerable.Range(1, 50).Select(i => Task(i, "Same title")).Reverse().ToList();
        var first = Ids(TaskSearch.Rank(tasks, "same", Now, 5));
        var second = Ids(TaskSearch.Rank(tasks.AsEnumerable().Reverse(), "same", Now, 5));

        Assert.Equal([1, 2, 3, 4, 5], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
    {
        Assert.Empty(TaskSearch.Rank([Task(1, "Alpha")], "zulu", Now, 10));
    }
}
