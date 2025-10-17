using System;
using System.Collections.Generic;
using RunCommandsService;
using Cronos;

namespace RunCommandsService
{
    /// <summary>
    /// Test class for timezone and cron expression improvements.
    /// Run this as: dotnet run --project RunCommandsService.csproj -- --run-tests
    /// Or call RunAllTests() from your own test runner.
    /// </summary>
    public class TimezoneTest
    {
        // Note: Main method removed to avoid conflict with Program.cs
        // Call RunAllTests() from your test runner or Program.cs with args check
        public static void RunAllTests()
        {
            Console.WriteLine("Testing timezone and cron expression improvements...\n");

            // Test timezone mapping
            TestTimezoneMapping();

            // Test cron expression parsing
            TestCronParsing();

            // Test next occurrence calculation
            TestNextOccurrence();

            Console.WriteLine("\nAll tests completed!");
        }

        private static void TestTimezoneMapping()
        {
            Console.WriteLine("=== Testing Timezone Mapping ===");
            
            var testZones = new List<string> 
            { 
                "America/New_York", 
                "Europe/London", 
                "Asia/Tokyo", 
                "Australia/Sydney",
                "Europe/Berlin",
                "America/Los_Angeles",
                "Invalid/Timezone",
                "",
                null
            };

            foreach (var zone in testZones)
            {
                try
                {
                    var tzInfo = TimeZoneHelper.FindTimeZone(zone);
                    Console.WriteLine($"'{zone ?? "null"}' -> '{tzInfo.Id}' (Display: {tzInfo.DisplayName})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"'{zone ?? "null"}' -> Error: {ex.Message}");
                }
            }
            Console.WriteLine();
        }

        private static void TestCronParsing()
        {
            Console.WriteLine("=== Testing Cron Expression Parsing ===");
            
            var testCrons = new List<string> 
            { 
                "0 9 * * *",           // Every day at 9 AM
                "0 23 * * 1-5",        // Weekdays at 11 PM
                "*/5 * * * *",         // Every 5 minutes
                "0 0 1 1 *",           // Every Jan 1 at midnight
                "invalid-cron",        // Invalid expression
                "",                    // Empty string
                null                   // Null
            };

            foreach (var cron in testCrons)
            {
                try
                {
                    if (string.IsNullOrEmpty(cron))
                    {
                        Console.WriteLine($"'{cron ?? "null"}' -> Invalid (empty/null)");
                    }
                    else
                    {
                        var expression = CronExpression.Parse(cron);
                        Console.WriteLine($"'{cron}' -> Valid");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"'{cron ?? "null"}' -> Error: {ex.Message}");
                }
            }
            Console.WriteLine();
        }

        private static void TestNextOccurrence()
        {
            Console.WriteLine("=== Testing Next Occurrence Calculation ===");
            
            var testCases = new[]
            {
                new { Cron = "0 9 * * *", TimeZone = "America/New_York" },
                new { Cron = "0 9 * * *", TimeZone = "Europe/London" },
                new { Cron = "0 9 * * *", TimeZone = "Asia/Tokyo" },
                new { Cron = "*/15 * * * *", TimeZone = "UTC" }
            };

            var now = DateTime.UtcNow;

            foreach (var testCase in testCases)
            {
                try
                {
                    var cron = CronExpression.Parse(testCase.Cron);
                    var tz = TimeZoneHelper.FindTimeZone(testCase.TimeZone);

                    // Use Cronos directly with UTC base time
                    var nextOccurrence = cron.GetNextOccurrence(DateTime.SpecifyKind(now, DateTimeKind.Utc), tz);

                    Console.WriteLine($"Cron '{testCase.Cron}' in '{testCase.TimeZone}' -> Next: {nextOccurrence?.ToString("o") ?? "null"}");

                    // Show local time too for comparison
                    if (nextOccurrence.HasValue)
                    {
                        var localTime = TimeZoneInfo.ConvertTimeFromUtc(nextOccurrence.Value, tz);
                        Console.WriteLine($"  -> Local time: {localTime:yyyy-MM-dd HH:mm:ss} ({tz.Id})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cron '{testCase.Cron}' in '{testCase.TimeZone}' -> Error: {ex.Message}");
                }
            }
            Console.WriteLine();
        }
    }
}