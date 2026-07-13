using System.Text.Json;
using EmsScout.Application.Logging;
using EmsScout.Infrastructure.Logging;

namespace EmsScout.Tests;

public sealed class NdjsonApplicationLoggerTests
{
    [Fact]
    public void WritesStructuredContextAndRedactsSecrets()
    {
        var root = TemporaryDirectory();
        var timestamp = new DateTimeOffset(2026, 7, 11, 8, 30, 0, TimeSpan.Zero);
        var logger = new NdjsonApplicationLogger(root, () => timestamp);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        logger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Error,
            "collection",
            "workflow_failed",
            $"failed in {home}/private?token=abc123",
            new ApplicationLogContext("wf-1", "import", "database_operation_failed", true),
            new InvalidOperationException(
                "Authorization: Bearer secret-token at https://example.com/ui?ticket=TOPSECRET#session"),
            new Dictionary<string, object?>
            {
                ["databasePath"] = home + "/ac.db",
                ["accessToken"] = "secret-value",
            }));

        var line = Assert.Single(File.ReadAllLines(logger.CurrentLogPath));
        using var document = JsonDocument.Parse(line);
        var record = document.RootElement;
        Assert.Equal("ERROR", record.GetProperty("level").GetString());
        Assert.Equal("collection", record.GetProperty("category").GetString());
        Assert.Equal("workflow_failed", record.GetProperty("event").GetString());
        Assert.Equal("wf-1", record.GetProperty("workflowId").GetString());
        Assert.Equal("import", record.GetProperty("stage").GetString());
        Assert.Equal("database_operation_failed", record.GetProperty("errorCode").GetString());
        Assert.True(record.GetProperty("retryable").GetBoolean());
        Assert.Contains("<user-home>", record.GetProperty("message").GetString());
        Assert.DoesNotContain("abc123", line);
        Assert.DoesNotContain("secret-token", line);
        Assert.DoesNotContain("secret-value", line);
        Assert.DoesNotContain("TOPSECRET", line);
        Assert.DoesNotContain("#session", line);
        Assert.Equal("<redacted>", record.GetProperty("data").GetProperty("accessToken").GetString());
    }

    [Fact]
    public void ConcurrentWritesRemainOneValidJsonObjectPerLine()
    {
        var root = TemporaryDirectory();
        var logger = new NdjsonApplicationLogger(
            root,
            () => new DateTimeOffset(2026, 7, 11, 8, 30, 0, TimeSpan.Zero));

        Parallel.For(0, 100, index => logger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Information,
            "test",
            "parallel_write",
            "row " + index)));

        var lines = File.ReadAllLines(logger.CurrentLogPath);
        Assert.Equal(100, lines.Length);
        Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
    }

    [Fact]
    public void TruncatesOversizedMessagesAndNeverThrowsForInvalidDestination()
    {
        var root = TemporaryDirectory();
        var logger = new NdjsonApplicationLogger(root);
        logger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Warning,
            "test",
            "large_message",
            new string('x', 5000)));

        var line = Assert.Single(File.ReadAllLines(logger.CurrentLogPath));
        Assert.Contains("[truncated]", line);

        var fileInsteadOfDirectory = Path.Combine(root, "not-a-directory");
        File.WriteAllText(fileInsteadOfDirectory, "occupied");
        var unavailable = new NdjsonApplicationLogger(fileInsteadOfDirectory);
        var exception = Record.Exception(() => unavailable.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Error,
            "test",
            "write_failure",
            "must not throw")));
        Assert.Null(exception);
    }

    [Fact]
    public void HonorsDisabledLoggingExceptForForcedEvents()
    {
        var root = TemporaryDirectory();
        var logger = new NdjsonApplicationLogger(root, enabled: () => false);

        logger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Information,
            "test",
            "disabled",
            "not written"));
        Assert.False(File.Exists(logger.CurrentLogPath));

        logger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Critical,
            "startup",
            "forced",
            "written",
            AlwaysWrite: true));
        Assert.Single(File.ReadAllLines(logger.CurrentLogPath));
    }

    [Fact]
    public void WriteFailureClassifiesAndPersistsTheErrorCode()
    {
        var root = TemporaryDirectory();
        var logger = new NdjsonApplicationLogger(root);

        var failure = logger.WriteFailure(
            new FileNotFoundException("missing private path"),
            "data",
            "refresh_failed");

        Assert.Equal("required_file_missing", failure.Code);
        var line = Assert.Single(File.ReadAllLines(logger.CurrentLogPath));
        using var document = JsonDocument.Parse(line);
        Assert.Equal("required_file_missing", document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("refresh_failed", document.RootElement.GetProperty("event").GetString());
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-native-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
