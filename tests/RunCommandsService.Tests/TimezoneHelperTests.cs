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
}
