using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

public class IconGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public IconGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Each state produces the correct named file ──────────────────────────────

    [Theory]
    [InlineData(TrayIconState.Unknown,  "icon-unknown-v3.ico")]
    [InlineData(TrayIconState.Bridged,  "icon-bridged-v3.ico")]
    [InlineData(TrayIconState.Fallback, "icon-fallback-v3.ico")]
    public void GenerateAndSave_CreatesExpectedFile(TrayIconState state, string expectedFileName)
    {
        var path = IconGenerator.GenerateAndSave(_dir, state);

        Assert.Equal(Path.Combine(_dir, expectedFileName), path);
        Assert.True(File.Exists(path), $"Expected icon file was not created: {path}");
        Assert.True(new FileInfo(path).Length > 0, "Icon file is empty");
    }

    // ── The three states write to three distinct files ───────────────────────────

    [Fact]
    public void GenerateAndSave_ThreeStates_ProduceDifferentFiles()
    {
        var p1 = IconGenerator.GenerateAndSave(_dir, TrayIconState.Unknown);
        var p2 = IconGenerator.GenerateAndSave(_dir, TrayIconState.Bridged);
        var p3 = IconGenerator.GenerateAndSave(_dir, TrayIconState.Fallback);

        Assert.NotEqual(p1, p2);
        Assert.NotEqual(p2, p3);
        Assert.NotEqual(p1, p3);
    }

    // ── Second call with the same state returns the same path without recreating ─

    [Theory]
    [InlineData(TrayIconState.Unknown)]
    [InlineData(TrayIconState.Bridged)]
    [InlineData(TrayIconState.Fallback)]
    public void GenerateAndSave_CalledTwice_ReturnsSamePathAndDoesNotRewrite(TrayIconState state)
    {
        var first    = IconGenerator.GenerateAndSave(_dir, state);
        var writtenAt = File.GetLastWriteTimeUtc(first);

        var second = IconGenerator.GenerateAndSave(_dir, state);

        Assert.Equal(first, second);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(second));
    }

    // ── Deleting the cached file causes it to be regenerated ─────────────────────

    [Fact]
    public void GenerateAndSave_AfterDeletion_RecreatesFile()
    {
        var path = IconGenerator.GenerateAndSave(_dir, TrayIconState.Bridged);
        File.Delete(path);

        var recreated = IconGenerator.GenerateAndSave(_dir, TrayIconState.Bridged);

        Assert.Equal(path, recreated);
        Assert.True(File.Exists(recreated));
    }
}
