using WorkTrace.Core.Management;

namespace WorkTrace.Tests.Core;

public class PerspectiveQuotesTests
{
    [Fact]
    public void Quotes_AreManyShortAndUnique()
    {
        Assert.True(PerspectiveQuotes.All.Count >= 40);
        Assert.Equal(PerspectiveQuotes.All.Count, PerspectiveQuotes.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(PerspectiveQuotes.All, q => Assert.InRange(q.Length, 10, 120));
        Assert.Equal(PerspectiveQuotes.All[0], PerspectiveQuotes.At(PerspectiveQuotes.All.Count)); // wraps around
    }
}
