using System.Text.Json;
namespace Nightcall;
internal static class TranscriptResponse
{
    // JSON is UTF-8. Bypass ResponseText's legacy Windows code-page conversion.
    public static string Parse(byte[] utf8)
    {
        using var result = JsonDocument.Parse(utf8);
        return result.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }
}
