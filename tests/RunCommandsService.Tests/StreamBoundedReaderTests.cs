using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace RunCommandsService.Tests;

public class StreamBoundedReaderTests
{
    [Fact]
    public async Task ReadBoundedStreamAsync_TruncatesOutput_WhenExceedingLimit()
    {
        var largeText = new string('A', 1000);
        using var reader = new StringReader(largeText);

        var buffer = new char[4096];
        var sb = new StringBuilder();
        int totalRead = 0;
        int read;
        int maxChars = 100;

        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if (totalRead + read > maxChars)
            {
                int allowed = maxChars - totalRead;
                if (allowed > 0)
                {
                    sb.Append(buffer, 0, allowed);
                }
                sb.Append($"\n... [Output truncated after {maxChars} characters]");
                break;
            }
            sb.Append(buffer, 0, read);
            totalRead += read;
        }

        var result = sb.ToString();
        Assert.StartsWith(new string('A', 100), result);
        Assert.Contains("Output truncated after 100 characters", result);
    }
}
