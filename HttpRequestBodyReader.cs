using System.Text;

namespace RunCommandsService;

public sealed class RequestBodyTooLargeException : IOException
{
    public RequestBodyTooLargeException(int maxBytes)
        : base($"Request body exceeds the configured limit of {maxBytes} bytes.")
    {
        MaxBytes = maxBytes;
    }

    public int MaxBytes { get; }
}

public sealed class UnsupportedMediaTypeException : Exception
{
    public UnsupportedMediaTypeException(string message) : base(message) { }
}

/// <summary>Reads an HTTP request body without allowing unbounded memory growth.</summary>
public static class HttpRequestBodyReader
{
    public static string Read(Stream input, Encoding encoding, long declaredLength, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        if (declaredLength > maxBytes)
            throw new RequestBodyTooLargeException(maxBytes);

        using var buffer = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var chunk = new byte[Math.Min(maxBytes, 8 * 1024)];

        while (true)
        {
            var remaining = (long)maxBytes - buffer.Length;
            var requested = checked((int)Math.Min(chunk.Length, remaining + 1));
            var read = input.Read(chunk, 0, requested);
            if (read == 0)
                break;

            if (buffer.Length + read > maxBytes)
                throw new RequestBodyTooLargeException(maxBytes);

            buffer.Write(chunk, 0, read);
        }

        return encoding.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}
