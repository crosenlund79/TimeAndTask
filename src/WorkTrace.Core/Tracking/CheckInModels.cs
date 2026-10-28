using WorkTrace.Core.Entities;

namespace WorkTrace.Core.Tracking;

/// <summary>What the user recorded in the check-in popup.</summary>
/// <param name="PromptId">The scheduled prompt being answered; null for a manual check-in.</param>
/// <param name="ObservedAtUtc">When the statement applies; null means "now".</param>
public sealed record CheckInRequest(
    int? PromptId,
    int? TaskId,
    string? Note,
    int? ChannelId = null,
    DateTime? ObservedAtUtc = null);

public sealed record ObservationSummary(
    int Id,
    DateTime ObservedAtUtc,
    DateTime CreatedAtUtc,
    ObservationOrigin Origin,
    int? TaskId,
    string? TaskTitle,
    string? ExternalId,
    int? ClientId,
    string? ClientName,
    int? ChannelId,
    string? ChannelName,
    string? Note,
    bool IsCorrected);

public enum TimelineEventKind
{
    CheckIn = 0,
    MissedCheckIn = 1,
    DismissedCheckIn = 2,
    SuppressedCheckIn = 3,
    TrackingStarted = 4,
    TrackingStopped = 5,
    Locked = 6,
    Unlocked = 7,
}

/// <summary>One line in the day's raw activity timeline (facts only, no estimation).</summary>
public sealed record TimelineEvent(DateTime AtUtc, TimelineEventKind Kind, string Title, string? Detail);

public interface ICheckInService
{
    /// <summary>
    /// Stores an observation, marks the answered prompt (if any) and records task usage.
    /// A check-in needs a task or a note.
    /// </summary>
    /// <exception cref="Validation.DomainValidationException">Invalid input; nothing stored.</exception>
    Task<ObservationSummary> RecordAsync(CheckInRequest request, CancellationToken cancellationToken = default);

    /// <summary>The most recent (by observed time) non-deleted observation, if any.</summary>
    Task<ObservationSummary?> GetLastObservationAsync(CancellationToken cancellationToken = default);

    /// <summary>Raw facts for one local day: check-ins, missed prompts, tracking and lock changes.</summary>
    Task<IReadOnlyList<TimelineEvent>> GetDayTimelineAsync(DateOnly localDate, TimeZoneInfo timeZone, CancellationToken cancellationToken = default);
}
