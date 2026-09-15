using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using Xunit;
using RunCommandsService;

namespace RunCommandsService.Tests;

public class SecretMaskerTests
{
    [Theory]
    [InlineData("CHANGE-ME", true)]
    [InlineData("CHANGE_ME", true)]
    [InlineData("admin", true)]
    [InlineData("password", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("StrongSecretKey123!", false)]
    public void IsDefaultSecret_IdentifiesPlaceholdersCorrectly(string? secret, bool expectedIsDefault)
    {
        var result = SecretMasker.IsDefaultSecret(secret);
        Assert.Equal(expectedIsDefault, result);
    }

    [Fact]
    public void Mask_MasksSensitiveStrings()
    {
        Assert.Equal("(none)", SecretMasker.Mask(null));
        Assert.Equal("***", SecretMasker.Mask("123"));
        Assert.Equal("St***y!", SecretMasker.Mask("StrongKey!"));
    }

    [Fact]
    public void MaskUrl_RedactsQueryParamsAndTokens()
    {
        var masked = SecretMasker.MaskUrl("https://hooks.slack.com/services/T00/B00/X000000?token=secret");
        Assert.DoesNotContain("secret", masked);
        Assert.Contains("***", masked);
    }

    [Fact]
    public void FixedTimeEquals_ComparesStringsCorrectly()
    {
        Assert.True(SecretMasker.FixedTimeEquals("MyAdminKey123", "MyAdminKey123"));
        Assert.False(SecretMasker.FixedTimeEquals("MyAdminKey123", "WrongKey"));
        Assert.False(SecretMasker.FixedTimeEquals(null, "Key"));
    }

    [Fact]
    public void ConfigValidator_ReportsSecurityWarning_ForDefaultAdminKey()
    {
        var configDict = new Dictionary<string, string?>
        {
            ["Monitoring:EnableHttpEndpoint"] = "true",
            ["Monitoring:AdminKey"] = "CHANGE-ME"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        var report = ConfigValidator.Validate(config);

        Assert.False(report.AllValid);
        Assert.NotEmpty(report.SecurityWarnings);
        Assert.Contains(report.SecurityWarnings, w => w.Contains("AdminKey"));
    }
}
