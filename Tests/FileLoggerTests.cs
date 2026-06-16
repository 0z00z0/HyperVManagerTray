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
}
