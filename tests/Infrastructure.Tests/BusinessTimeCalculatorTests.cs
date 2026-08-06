using Modules.Helpdesk.Data;
using Modules.Helpdesk.Features.Sla;

namespace Infrastructure.Tests;

public sealed class BusinessTimeCalculatorTests
{
    private static readonly BusinessHoursCalendar Calendar = new()
    {
        TimeZoneId = "UTC", WorkingDays = BusinessDays.Weekdays,
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0),
    };

    [Fact]
    public void Elapsed_OvernightAndWeekend_CountsOnlyBusinessWindow()
    {
        var from = new DateTimeOffset(2026, 8, 7, 16, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(2), BusinessTimeCalculator.Elapsed(from, to, Calendar));
    }

    [Fact]
    public void Add_StartingAfterHours_ResumesNextBusinessDay()
    {
        var from = new DateTimeOffset(2026, 8, 7, 18, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero),
            BusinessTimeCalculator.Add(from, TimeSpan.FromHours(1), Calendar));
    }

    [Fact]
    public void Elapsed_ReversedInterval_ReturnsZero()
    {
        var instant = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, BusinessTimeCalculator.Elapsed(instant, instant.AddMinutes(-1), Calendar));
    }
}
