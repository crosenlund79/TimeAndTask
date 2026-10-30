namespace WorkTrace.Core.Guides;

public sealed record GuideSummary(int Id, string Title, string Category, DateTime CreatedAtUtc, DateTime ModifiedAtUtc);

public sealed record GuideDetail(int Id, string Title, string Category, string Content, DateTime CreatedAtUtc, DateTime ModifiedAtUtc);

/// <summary>Personal guides. Search matches title, category and content (case- and accent-insensitive).</summary>
public interface IGuideService
{
    /// <param name="category">Exact category (case-insensitive); null = all.</param>
    Task<IReadOnlyList<GuideSummary>> SearchAsync(string? text, string? category, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    Task<GuideDetail> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<GuideDetail> CreateAsync(string? title, string? category, CancellationToken cancellationToken = default);

    /// <summary>Saves the guide; the modified date only changes when something actually changed.</summary>
    Task<GuideDetail> UpdateAsync(int id, string? title, string? category, string? content, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
