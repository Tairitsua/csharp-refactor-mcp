using System.Text.Json;

internal static class DiagnosticTrace
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    internal static bool IsEnabled { get; } = ReadEnabled();

    internal static void Log(string area, string message, object? payload = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var entry = new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            area,
            message,
            payload
        };

        lock (SyncRoot)
        {
            Console.Error.WriteLine($"[refactor-mcp-debug] {JsonSerializer.Serialize(entry, JsonOptions)}");
        }
    }

    private static bool ReadEnabled()
    {
        var value = Environment.GetEnvironmentVariable("REFACTOR_MCP_DEBUG_SYMBOLS");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }
}
