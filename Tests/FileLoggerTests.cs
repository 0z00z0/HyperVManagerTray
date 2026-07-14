using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HyperVManagerTray.Tests;

public class FileLoggerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best-effort */ }
    }

    // The resume-from-standby race: a second instance must be able to open the same log while
    // the first still holds it (FileShare.ReadWrite), instead of throwing at startup.
    [Fact]
    public void TwoProviders_OnSamePath_DoNotThrow()
    {
        using var p1 = new FileLoggerProvider(_path);
        using var p2 = new FileLoggerProvider(_path);

        p1.CreateLogger("a").LogInformation("from instance 1");
        p2.CreateLogger("b").LogInformation("from instance 2");
    }

    // A lock that can't be share-negotiated (exclusive FileShare.None held elsewhere) must never
    // crash startup — the provider retries, then degrades to a no-op writer.
    [Fact]
    public void Provider_WhenFileExclusivelyLocked_DegradesWithoutThrowing()
    {
        using var exclusive = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);

        var ex = Record.Exception(() =>
        {
            using var p = new FileLoggerProvider(_path);
            p.CreateLogger("x").LogInformation("discarded");
        });

        Assert.Null(ex);
    }

    // IsEnabled: every level is accepted except None (the provider defers all other filtering to
    // the LoggerFactory's configured minimum level).
    [Theory]
    [InlineData(LogLevel.Trace,       true)]
    [InlineData(LogLevel.Debug,       true)]
    [InlineData(LogLevel.Information, true)]
    [InlineData(LogLevel.Warning,     true)]
    [InlineData(LogLevel.Error,       true)]
    [InlineData(LogLevel.Critical,    true)]
    [InlineData(LogLevel.None,        false)]
    public void Logger_IsEnabled_TrueForEveryLevelExceptNone(LogLevel level, bool expected)
    {
        using var p = new FileLoggerProvider(_path);
        var logger = p.CreateLogger("cat");
        Assert.Equal(expected, logger.IsEnabled(level));
    }

    // Log() must no-op (not throw, not write) when the level is disabled (None) — BeginScope
    // returning null must also not blow up a caller that disposes it.
    [Fact]
    public void Logger_LogWithDisabledLevel_DoesNotWrite()
    {
        using (var p = new FileLoggerProvider(_path))
        {
            var logger = p.CreateLogger("cat");
            using var scope = logger.BeginScope("scope-state");
            logger.Log(LogLevel.None, new EventId(1), "state", null, (s, e) => "should not appear");
        }

        var text = File.Exists(_path) ? File.ReadAllText(_path) : "";
        Assert.DoesNotContain("should not appear", text);
    }
}
