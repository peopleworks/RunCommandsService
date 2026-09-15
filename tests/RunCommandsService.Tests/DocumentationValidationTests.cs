using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using RunCommandsService;

namespace RunCommandsService.Tests;

public class DocumentationValidationTests
{
    private static string GetRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "RunCommandsService.csproj")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }
        throw new InvalidOperationException("Could not find repository root containing RunCommandsService.csproj");
    }

    [Fact]
    public void AppSettingsExample_IsValidJson_And_PassesValidation()
    {
        var repoRoot = GetRepoRoot();
        var examplePath = Path.Combine(repoRoot, "appsettings.example.json");

        Assert.True(File.Exists(examplePath), "appsettings.example.json should exist in repo root");

        var jsonText = File.ReadAllText(examplePath);
        using var doc = JsonDocument.Parse(jsonText);
        Assert.NotNull(doc);

        var config = new ConfigurationBuilder()
            .AddJsonFile(examplePath, optional: false)
            .Build();

        var report = ConfigValidator.Validate(config);
        Assert.True(report.AllValid, $"appsettings.example.json failed validation: {ConfigValidator.FormatReport(report)}");
    }

    [Fact]
    public void Documentation_HasNoOutdatedProjectPaths()
    {
        var repoRoot = GetRepoRoot();
        var mdFiles = new[] { "README.md", "AGENTS.md", "CONTRIBUTING.md" };

        foreach (var file in mdFiles)
        {
            var fullPath = Path.Combine(repoRoot, file);
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath);
            Assert.DoesNotContain(@"\RunCommandsService\RunCommandsService.csproj", content);
        }
    }
}
