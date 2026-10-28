namespace WorkTrace.Core.Entities;

/// <summary>
/// A period during which the Windows session was locked. Locked time is never treated as
/// confirmed work; how it is classified is controlled by tracking settings.
/// </summary>
public class LockPeriod
{
    public int Id { get; set; }

    public DateTime StartUtc { get; set; }

    /// <summary>Null while the session is still locked (or the app stopped while locked).</summary>
    public DateTime? EndUtc { get; set; }
}
