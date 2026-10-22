using Insys.Workflow.Domain.Approvals;
using Insys.Workflow.Domain.Processes;

namespace Insys.Workflow.Tests;

/// <summary>SLA time on business calendars (OQ-049).</summary>
public sealed class BusinessCalendarTests
{
    private static readonly string[] Weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri"];

    // Johannesburg is UTC+2 all year.
    private static BusinessCalendar Office(params DateOnly[] holidays) =>
        BusinessCalendar.Create("ZA-OFFICE", Weekdays, "08:00–17:00", "Africa/Johannesburg", holidays).Value;

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void WorkingTime_SkipsEveningsWeekendsAndHolidays()
    {
        // Thursday 24 December, 16:00 local: one hour on Thursday; Christmas (Friday) and the weekend are skipped;
        // three hours on Monday 28 December from 08:00 local, so 11:00 local (09:00 UTC).
        var due = Office(new DateOnly(2026, 12, 25)).AddWorkingTime(Utc(12, 24, 14), TimeSpan.FromHours(4));
        due.ShouldBe(Utc(12, 28, 9));
    }

    [Fact]
    public void WorkingTime_StartingOutsideHours_StartsAtTheNextOpening() =>
        Office().AddWorkingTime(Utc(10, 24, 10), TimeSpan.FromMinutes(30)).ShouldBe(Utc(10, 26, 6, 30));

    [Fact]
    public void WholeDays_AreWorkingDays() =>
        Office().Add(Utc(10, 23, 8), days: 2, TimeSpan.Zero).ShouldBe(Utc(10, 27, 8));

    [Fact]
    public void RoundTheClockCalendar_IsClockTime() =>
        BusinessCalendar.Create("ZA-24X7", ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], "00:00–24:00", "Africa/Johannesburg", []).Value
            .AddWorkingTime(Utc(10, 24, 22), TimeSpan.FromHours(4)).ShouldBe(Utc(10, 25, 2));

    [Theory]
    [InlineData("17:00–08:00", "Africa/Johannesburg")]
    [InlineData("08:00–17:00", "Mars/Olympus")]
    [InlineData("eight to five", "Africa/Johannesburg")]
    public void Calendar_WithoutUsableWorkingTime_IsRefused(string hours, string zone) =>
        BusinessCalendar.Create("BAD", Weekdays, hours, zone, []).Error!.Code.ShouldBe(WorkflowErrorCodes.DefinitionInvalid);
}
