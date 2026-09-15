using System;
using Xunit;
using RunCommandsService;
using Cronos;

namespace RunCommandsService.Tests;

public class TimezoneHelperTests
{
    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Tokyo")]
    [InlineData("Australia/Sydney")]
    [InlineData("Europe/Berlin")]
    [InlineData("America/Los_Angeles")]
    public void FindTimeZone_ValidZones_ReturnsTimeZoneInfo(string zone)
    {
        var tzInfo = TimeZoneHelper.FindTimeZone(zone);
        Assert.NotNull(tzInfo);
    }

    [Theory]
    [InlineData("Invalid/Timezone")]
    [InlineData("")]
    [InlineData(null)]
    public void FindTimeZone_InvalidZones_ReturnsUtc(string? zone)
    {
        var tzInfo = TimeZoneHelper.FindTimeZone(zone);
        Assert.Equal(TimeZoneInfo.Utc, tzInfo);
    }

    [Theory]
    [InlineData("America/New_York", true)]
    [InlineData("Eastern Standard Time", true)]
    [InlineData("UTC", true)]
    [InlineData("Invalid/Zone", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidTimeZone_ReturnsExpectedResult(string? zone, bool expectedValid)
    {
        var isValid = TimeZoneHelper.IsValidTimeZone(zone!, out var error);
        Assert.Equal(expectedValid, isValid);
        if (expectedValid)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData("0 9 * * *")]
    [InlineData("0 23 * * 1-5")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 0 1 1 *")]
    public void CronExpression_Parse_Valid(string cron)
    {
        var expression = CronExpression.Parse(cron);
        Assert.NotNull(expression);
    }

    [Theory]
    [InlineData("invalid-cron")]
    public void CronExpression_Parse_Invalid(string cron)
    {
        Assert.ThrowsAny<Exception>(() => CronExpression.Parse(cron));
    }

    [Fact]
    public void CronExpression_Parse_Null_Throws()
    {
        Assert.ThrowsAny<Exception>(() => CronExpression.Parse(null!));
    }

    [Theory]
    [InlineData("0 9 * * *", "America/New_York")]
    [InlineData("0 9 * * *", "Europe/London")]
    [InlineData("0 9 * * *", "Asia/Tokyo")]
    [InlineData("*/15 * * * *", "UTC")]
    public void GetNextOccurrence_ReturnsExpected(string cronStr, string tzStr)
    {
        var cron = CronExpression.Parse(cronStr);
        var tz = TimeZoneHelper.FindTimeZone(tzStr);
        var now = DateTime.UtcNow;

        var next = cron.GetNextOccurrence(now, tz);
        
        Assert.True(next.HasValue);
        Assert.True(next.Value > now);
        Assert.Equal(DateTimeKind.Utc, next.Value.Kind);
    }

    [Fact]
    public void DaylightSavingTime_SpringForward_HandlesNonExistentLocalTime()
    {
        // 2:30 AM on March 8, 2026 does not exist in Eastern Standard Time (spring forward from 2:00 AM to 3:00 AM)
        var cron = CronExpression.Parse("30 2 8 3 *");
        var tz = TimeZoneHelper.FindTimeZone("America/New_York");
        var fromTimeUtc = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc);

        var nextOccurrence = cron.GetNextOccurrence(fromTimeUtc, tz);

        Assert.True(nextOccurrence.HasValue);
        Assert.Equal(DateTimeKind.Utc, nextOccurrence.Value.Kind);
    }

    [Fact]
    public void DaylightSavingTime_FallBack_HandlesAmbiguousLocalTime()
    {
        // 1:30 AM on November 1, 2026 is ambiguous in Eastern Standard Time (fall back from 2:00 AM to 1:00 AM)
        var cron = CronExpression.Parse("30 1 1 11 *");
        var tz = TimeZoneHelper.FindTimeZone("America/New_York");
        var fromTimeUtc = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

        var nextOccurrence = cron.GetNextOccurrence(fromTimeUtc, tz);

        Assert.True(nextOccurrence.HasValue);
        Assert.Equal(DateTimeKind.Utc, nextOccurrence.Value.Kind);
    }
}
