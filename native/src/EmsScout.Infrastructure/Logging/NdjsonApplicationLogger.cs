using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmsScout.Application.Logging;

namespace EmsScout.Infrastructure.Logging;

public sealed partial class NdjsonApplicationLogger : IApplicationLogger
{
    private const int MessageLimit = 4096;
    private const int DetailLimit = 16384;
    private const int DataValueLimit = 4096;
    private readonly object writeLock = new();
    private readonly string logDirectory;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<bool> enabled;
    private readonly string userHome;

    public NdjsonApplicationLogger(
        string logDirectory,
        Func<DateTimeOffset>? clock = null,
        Func<bool>? enabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.logDirectory = Path.GetFullPath(logDirectory);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.enabled = enabled ?? (() => true);
        userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string CurrentLogPath => LogPath(clock());

    public void Write(ApplicationLogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        try
        {
            if (!logEvent.AlwaysWrite && !enabled())
            {
                return;
            }

            var timestamp = clock().ToUniversalTime();
            var record = new
            {
                timestamp,
                level = logEvent.Level.ToString().ToUpperInvariant(),
                category = NormalizeName(logEvent.Category, "application"),
                @event = NormalizeName(logEvent.EventName, "message"),
                message = Sanitize(logEvent.Message, MessageLimit),
                processId = Environment.ProcessId,
                workflowId = SanitizeOptional(logEvent.Context?.WorkflowId, 128),
                stage = SanitizeOptional(logEvent.Context?.Stage, 128),
                errorCode = SanitizeOptional(logEvent.Context?.ErrorCode, 128),
                retryable = logEvent.Context?.Retryable,
                exceptionType = logEvent.Exception?.GetType().FullName,
                exceptionMessage = SanitizeOptional(logEvent.Exception?.Message, MessageLimit),
                detail = SanitizeOptional(logEvent.Exception?.ToString(), DetailLimit),
                data = SanitizeData(logEvent.Data),
            };
            var json = JsonSerializer.Serialize(record, JsonOptions);
            lock (writeLock)
            {
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(LogPath(timestamp), json + Environment.NewLine);
            }
        }
        catch
        {
            // Logging is best effort and must never replace the product operation.
        }
    }

    private IReadOnlyDictionary<string, object?>? SanitizeData(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null || data.Count == 0)
        {
            return null;
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in data)
        {
            output[NormalizeName(pair.Key, "value")] = IsSensitiveKey(pair.Key)
                ? "<redacted>"
                : Sanitize(
                    Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    DataValueLimit);
        }

        return output;
    }

    private string? SanitizeOptional(string? value, int limit)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Sanitize(value, limit);
    }

    private string Sanitize(string value, int limit)
    {
        var output = value;
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            output = output.Replace(userHome, "<user-home>", StringComparison.OrdinalIgnoreCase);
        }

        output = BearerTokenRegex().Replace(output, "$1<redacted>");
        output = UrlQueryOrFragmentRegex().Replace(output, "$1");
        output = SecretQueryRegex().Replace(output, "$1<redacted>");
        return output.Length <= limit ? output : output[..limit] + "...[truncated]";
    }

    private string LogPath(DateTimeOffset timestamp)
    {
        return Path.Combine(logDirectory, $"native-{timestamp.UtcDateTime:yyyy-MM-dd}.ndjson");
    }

    private static string NormalizeName(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var output = new string(value.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_').ToArray());
        return output.Length <= 128 ? output : output[..128];
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+\-/]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)([?&](?:token|access_token|password|session|secret)=)[^&#\s]+")]
    private static partial Regex SecretQueryRegex();

    [GeneratedRegex("""(?i)(https?://[^\s?#"']+)[?#][^\s"'<>]*""")]
    private static partial Regex UrlQueryOrFragmentRegex();
}
