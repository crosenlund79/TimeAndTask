namespace WorkTrace.Core.Entities;

public enum CheckInOutcome
{
    /// <summary>Prompt is (or was) on screen and has not been resolved yet.</summary>
    Pending = 0,

    /// <summary>The user recorded an observation.</summary>
    Answered = 1,

    /// <summary>The prompt timed out without an answer.</summary>
    Missed = 2,

    /// <summary>The user explicitly dismissed the prompt (Escape) without confirming.</summary>
    Dismissed = 3,

    /// <summary>The prompt was due but could not be shown (e.g. session locked).</summary>
    Suppressed = 4,
}

/// <summary>
/// A scheduled check-in. Missed/dismissed/suppressed prompts are recorded as facts: they mark the
/// point from which time becomes unclassified rather than being attributed to the previous task.
/// </summary>
public class CheckInPrompt
{
    public int Id { get; set; }

    public DateTime ScheduledAtUtc { get; set; }

    public DateTime? ShownAtUtc { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public CheckInOutcome Outcome { get; set; }

    public int? ObservationId { get; set; }

    public Observation? Observation { get; set; }

    /// <summary>Outcomes that make subsequent time uncertain.</summary>
    public bool CreatesUncertainty => Outcome is CheckInOutcome.Missed or CheckInOutcome.Dismissed or CheckInOutcome.Suppressed;
}
