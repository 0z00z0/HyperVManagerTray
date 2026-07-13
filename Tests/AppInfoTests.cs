using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

public class AppInfoTests
{
    // ── FormatVersion: pure formatting used for the About window and the tray hover tooltip ────

    [Fact]
    public void FormatVersion_ThreeComponentVersion_ReturnsThreeComponentString()
        => Assert.Equal("2.3.0", AppInfo.FormatVersion(new Version(2, 3, 0)));

    [Fact]
    public void FormatVersion_FourComponentVersion_TruncatesToThreeComponents()
        => Assert.Equal("2.3.0", AppInfo.FormatVersion(new Version(2, 3, 0, 42)));

    [Fact]
    public void FormatVersion_ZeroVersion_ReturnsZeroDotZeroDotZero()
        => Assert.Equal("0.0.0", AppInfo.FormatVersion(new Version(0, 0, 0)));

    [Fact]
    public void FormatVersion_NullVersion_ReturnsUnknown()
        => Assert.Equal("unknown", AppInfo.FormatVersion(null));
}
