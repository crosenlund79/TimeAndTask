namespace WorkTrace.Core.Management;

public enum DeadlineState
{
    None = 0,

    /// <summary>Within the warning period.</summary>
    Soon = 1,

    /// <summary>Past the deadline and not completed.</summary>
    Overdue = 2,
}

public static class Deadlines
{
    /// <summary>Warning state of a deadline; completed work (archived task, Done card) has none.</summary>
    public static DeadlineState State(DateOnly? deadline, DateOnly today, int warningDays, bool completed = false) =>
        completed || deadline is not { } d ? DeadlineState.None
        : d < today ? DeadlineState.Overdue
        : d.DayNumber - today.DayNumber <= warningDays ? DeadlineState.Soon
        : DeadlineState.None;
}
