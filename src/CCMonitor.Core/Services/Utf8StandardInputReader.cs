using System.Text;

namespace CCMonitor.Core.Services;

public static class Utf8StandardInputReader
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public static Task<string> ReadAsync()
        => ReadAsync(Console.OpenStandardInput());

    public static async Task<string> ReadAsync(Stream input)
    {
        using var reader = new StreamReader(
            input,
            Utf8WithoutBom,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
