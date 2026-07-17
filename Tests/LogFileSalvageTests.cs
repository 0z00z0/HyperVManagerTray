using System.Text;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #55: <c>switcher.log</c> on the dev machine opens with ~705 KB of NUL bytes — an append that
/// NTFS sized but whose blocks a power cut never committed. Rotation bounds a log's growth; it does
/// nothing for a file that is already damaged, and NLog's own writes can produce the same damage again.
/// These cover the repair.
/// </summary>
public class LogFileSalvageTests : IDisposable
{
    private readonly List<string> _paths = [];

    private string TempPath()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
        _paths.Add(p);
        return p;
    }

    /// <summary>Writes <paramref name="nulCount"/> NUL bytes followed by <paramref name="tail"/>.</summary>
    private string NulHeadedFile(int nulCount, string tail)
    {
        var path = TempPath();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[nulCount]);
        fs.Write(Encoding.UTF8.GetBytes(tail));
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            try { File.Delete(p); } catch { /* best-effort */ }
    }

    // The reported shape, at the reported scale: a big NUL head, then real log lines. The NULs go, the
    // log survives — the whole point is that Espen keeps a readable history rather than a bounded ruin.
    [Fact]
    public void TrySalvage_NulHeadedFile_StripsTheNulsAndKeepsTheLogContent()
    {
        var tail = "2026-07-17 09:00:00.0000 [Information] cat: first real line\n"
                 + "2026-07-17 09:00:01.0000 [Warning    ] cat: second real line\n";
        var path = NulHeadedFile(705 * 1024, "partial-garbage-line\n" + tail);

        Assert.True(LogFileSalvage.TrySalvage(path));

        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain((byte)0, bytes);
        Assert.Equal(tail, Encoding.UTF8.GetString(bytes));
    }

    // The first surviving byte lands mid-line, so the repaired file starts at the next line boundary
    // rather than opening on a fragment.
    [Fact]
    public void TrySalvage_StartsTheRepairedFileOnAWholeLine()
    {
        var path = NulHeadedFile(1024, "ne-line\n2026-07-17 09:00:00.0000 [Information] cat: whole line\n");

        Assert.True(LogFileSalvage.TrySalvage(path));

        var text = File.ReadAllText(path);
        Assert.StartsWith("2026-07-17", text);
        Assert.DoesNotContain("ne-line", text);
    }

    // A healthy log must be left exactly alone — this runs at every startup, over live data.
    [Fact]
    public void TrySalvage_HealthyFile_ReturnsFalseAndChangesNothing()
    {
        var path     = TempPath();
        var original = "2026-07-17 09:00:00.0000 [Information] cat: perfectly fine\n";
        File.WriteAllText(path, original);

        Assert.False(LogFileSalvage.TrySalvage(path));
        Assert.Equal(original, File.ReadAllText(path));
    }

    // Nothing but NULs: there is no history to keep, so the file is emptied rather than left as a wall
    // of zeros that makes the log look full while saying nothing.
    [Fact]
    public void TrySalvage_AllNulFile_TruncatesItToEmpty()
    {
        var path = NulHeadedFile(64 * 1024, "");

        Assert.True(LogFileSalvage.TrySalvage(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    // A NUL run whose only surviving bytes are a trailing newline would salvage to an empty file; the
    // partial line is kept instead, since a fragment of real content beats none.
    [Fact]
    public void TrySalvage_WhenOnlyATrailingNewlineSurvives_KeepsThePartialLine()
    {
        var path = NulHeadedFile(1024, "fragment\n");

        Assert.True(LogFileSalvage.TrySalvage(path));
        Assert.Equal("fragment\n", File.ReadAllText(path));
    }

    [Fact]
    public void TrySalvage_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".log");
        Assert.False(LogFileSalvage.TrySalvage(path));
    }

    [Fact]
    public void TrySalvage_EmptyFile_ReturnsFalse()
    {
        var path = TempPath();
        File.WriteAllBytes(path, []);
        Assert.False(LogFileSalvage.TrySalvage(path));
    }

    // Repairing a file another process holds would race its appends. Backing off — not throwing, and
    // above all not half-rewriting it — is the required behaviour.
    [Fact]
    public void TrySalvage_LockedFile_ReturnsFalseAndLeavesItUntouched()
    {
        var path = NulHeadedFile(4096, "still here\n");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(LogFileSalvage.TrySalvage(path));

        held.Position = 0;
        var bytes = new byte[held.Length];
        held.ReadExactly(bytes);
        Assert.Equal(4096 + "still here\n".Length, bytes.Length);   // untouched
    }

    // The wiring: the provider must repair each of its logs BEFORE NLog opens a handle on it, or the
    // salvage would be racing the target's own writes and could never take the file exclusively.
    [Fact]
    public void FileLoggerProvider_SalvagesEveryLogItOwns_BeforeWritingToThem()
    {
        var defaultPath = NulHeadedFile(8192, "old switcher line\n");
        var vmPowerPath = NulHeadedFile(8192, "old power line\n");
        var map = new Dictionary<string, string> { ["vm-power"] = vmPowerPath };

        using (var p = new FileLoggerProvider(defaultPath, map))
        {
            p.CreateLogger("cat").LogInformation("new switcher line");
            p.CreateLogger("vm-power").LogInformation("new power line");
        }

        foreach (var (path, oldLine, newLine) in
                 new[] { (defaultPath, "old switcher line", "new switcher line"),
                         (vmPowerPath, "old power line",    "new power line") })
        {
            Assert.DoesNotContain((byte)0, File.ReadAllBytes(path));
            var text = File.ReadAllText(path);
            Assert.Contains(oldLine, text);   // history salvaged, not thrown away …
            Assert.Contains(newLine, text);   // … and the session's own lines appended after it
        }
    }
}
