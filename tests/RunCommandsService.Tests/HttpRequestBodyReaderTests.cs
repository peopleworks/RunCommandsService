using System.Text;
using RunCommandsService;

namespace RunCommandsService.Tests;

public class HttpRequestBodyReaderTests
{
    [Fact]
    public void Read_ReturnsBody_WhenExactlyAtLimit()
    {
        var bytes = Encoding.UTF8.GetBytes("12345678");
        using var stream = new MemoryStream(bytes);

        var result = HttpRequestBodyReader.Read(stream, Encoding.UTF8, bytes.Length, bytes.Length);

        Assert.Equal("12345678", result);
    }

    [Fact]
    public void Read_RejectsDeclaredLengthAboveLimit()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        var error = Assert.Throws<RequestBodyTooLargeException>(
            () => HttpRequestBodyReader.Read(stream, Encoding.UTF8, 100, 10));

        Assert.Equal(10, error.MaxBytes);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Read_RejectsChunkedBodyThatCrossesLimit()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345678901"));

        Assert.Throws<RequestBodyTooLargeException>(
            () => HttpRequestBodyReader.Read(stream, Encoding.UTF8, -1, 10));
    }
}
