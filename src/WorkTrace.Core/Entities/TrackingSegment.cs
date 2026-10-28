namespace WorkTrace.Core.Entities;

public enum TrackingEndReason
{
    /// <summary>Segment is still open.</summary>
    None = 0,
    Paused = 1,
    Shutdown = 2,
    Sleep = 3,

    /// <summary>The application terminated without closing the segment; end taken from the last heartbeat.</summary>
    Crash = 4,

    /// <summary>The system clock jumped; segments are split so estimation never spans the jump.</summary>
    ClockChange = 5,
}

/// <summary>
/// A contiguous period during which WorkTrace was running with tracking enabled.
/// Estimation never extends outside, or across the boundary between, segments.
/// </summary>
public class TrackingSegment
{
    public int Id { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    /// <summary>Last time the running application confirmed the segment was alive.</summary>
    public DateTime LastHeartbeatUtc { get; set; }

    public TrackingEndReason EndReason { get; set; }

    public bool IsOpen => EndUtc is null;
}
