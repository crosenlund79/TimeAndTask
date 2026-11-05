using WorkTrace.Core.Management;

namespace WorkTrace.Tests.Core;

public class PlanningCalendarTests
{
    [Theory]
    [InlineData("2026-01-01", HolidayKind.Public)]   // Nytårsdag
    [InlineData("2026-04-02", HolidayKind.Public)]   // Skærtorsdag (Easter 5 April 2026)
    [InlineData("2026-04-06", HolidayKind.Public)]   // 2. påskedag
    [InlineData("2026-05-14", HolidayKind.Public)]   // Kristi himmelfartsdag
    [InlineData("2026-05-25", HolidayKind.Public)]   // 2. pinsedag
    [InlineData("2026-12-25", HolidayKind.Public)]
    [InlineData("2023-05-05", HolidayKind.Public)]   // Store bededag, before it was abolished
    [InlineData("2026-05-01", HolidayKind.Customary)] // Store bededag 2026: abolished
    [InlineData("2026-06-05", HolidayKind.Customary)] // Grundlovsdag
    [InlineData("2026-12-24", HolidayKind.Customary)]
    public void DanishHolidays_AreConfirmedOrCustomary(string date, HolidayKind kind) =>
        Assert.Equal(kind, DanishHolidays.Get(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture))?.Kind);

    [Fact]
    public void Pipeline_UsesWorkingDays_SkipsWeekendsAndHolidays_AndRespectsPlannedStarts()
    {
        TaskSummary Task(int id, int? minutes, DateOnly? planned = null, bool recurring = false) => new(
            id, 1, "S", true, null, null, null, $"T{id}", null, null, null, false, DateTime.UnixEpoch, DateTime.UnixEpoch, null,
            IsRecurring: recurring, EstimatedRemainingMinutes: minutes, PlannedStart: planned);

        // Thursday 24 Dec 2026 (customary) → Friday 25 is Juledag; Monday 28 is the next working day after the weekend.
        var plan = TaskPipeline.Plan(
            [Task(1, 600), Task(2, null, planned: new DateOnly(2027, 1, 5)), Task(3, 60, recurring: true)],
            new DateOnly(2026, 12, 24));

        var first = plan.Entries[0];
        Assert.Equal((0.0, new DateOnly(2026, 12, 24)), (first.StartDay, first.StartDate));
        Assert.Equal(new DateOnly(2026, 12, 28), first.EndDate);          // 7.5 h on the 24th, Juledag skipped, 2.5 h on the 28th
        Assert.Equal(0, plan.Days[1].CapacityMinutes);                     // Friday 25 December
        var second = plan.Entries[1];
        Assert.Equal((2, new DateOnly(2027, 1, 5), true), (second.Task.Id, second.StartDate, second.IsProvisional)); // at its planned start
        Assert.Equal(2, plan.Entries.Count);                               // recurring tasks are not active work
        Assert.DoesNotContain(plan.Days, d => d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
