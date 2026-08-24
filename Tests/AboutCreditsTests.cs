using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HyperVManagerTray.Helpers;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The rule <see cref="AppAbout"/>'s own doc comment states but nothing enforced: <b>every package the
/// app references is credited in the About window.</b>
///
/// <para>Three lists have to agree — the csproj's <c>PackageReference</c> items, the credits in
/// <see cref="AppAbout.CreateInfo"/>, and the README's "External libraries" table. Two of them drifted:
/// NLog (issue #55) and TaskScheduler (issue #61) were added to the build and never credited, and the
/// README missed TaskScheduler as well. Adding a package is a one-line csproj edit that gives no reason
/// to open either of the other two, so the gap reappears unless a test holds the three together.</para>
///
/// <para>The csproj and the README are read as text — the same approach
/// <see cref="InstallerSilentInstallTests"/> takes to the Inno script, and for the same reason: neither
/// has any other automated reader. The credits side is the real
/// <see cref="AppAbout.CreateInfo"/>, linked into this assembly (see the test csproj), so the assertion
/// is about the list the About window actually renders rather than a copy of it.</para>
/// </summary>
public class AboutCreditsTests
{
    /// <summary>
    /// Packages deliberately NOT credited, each with the reason. Empty on purpose: every package the
    /// app references today ships in, or is required to build, what the user runs, so every one is
    /// credited. A build-only tool that genuinely warrants no credit belongs here with its reason —
    /// the point of the dictionary is that an exclusion has to be written down to take effect, so it
    /// can be argued with, rather than being an unexplained hole in the list.
    /// </summary>
    private static readonly Dictionary<string, string> NotCredited = new();

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>Every <c>PackageReference</c> Include name in the app's csproj, read from
    /// comment-stripped text so a commented-out reference cannot satisfy the assertion.</summary>
    private static string[] PackageReferences()
    {
        var xml = Regex.Replace(ReadRepoFile("HyperVManagerTray.csproj"), "<!--.*?-->", string.Empty,
                                RegexOptions.Singleline);
        var names = Regex.Matches(xml, @"<PackageReference\s+Include=""([^""]+)""")
                         .Select(m => m.Groups[1].Value)
                         .ToArray();

        // A parse that silently matched nothing would make every assertion below vacuously true.
        Assert.NotEmpty(names);
        return names;
    }

    private static string[] CreditedNames() =>
        AppAbout.CreateInfo().ExternalLibraries.Select(l => l.Name).ToArray();

    /// <summary>THE test for the defect: a package added to the build but never credited.</summary>
    [Fact]
    public void EveryPackageReferenceIsCreditedInTheAboutWindow()
    {
        var credited = CreditedNames();
        var missing  = PackageReferences()
                       .Where(p => !NotCredited.ContainsKey(p))
                       .Where(p => !credited.Contains(p, StringComparer.OrdinalIgnoreCase))
                       .ToArray();

        Assert.True(missing.Length == 0,
            $"Referenced but not credited in Helpers\\AppAbout.cs: {string.Join(", ", missing)}. " +
            "Add an ExternalLibrary entry (and a README row), or record a reason in NotCredited.");
    }

    /// <summary>
    /// The other direction: a credit that outlived its package. A dependency dropped from the csproj
    /// leaves the About window claiming a library the app no longer ships — the same drift, mirrored.
    /// </summary>
    [Fact]
    public void EveryCreditedLibraryIsStillReferenced()
    {
        var referenced = PackageReferences();
        var stale      = CreditedNames()
                         .Where(c => !referenced.Contains(c, StringComparer.OrdinalIgnoreCase))
                         .ToArray();

        Assert.True(stale.Length == 0,
            $"Credited in Helpers\\AppAbout.cs but no longer referenced by the csproj: {string.Join(", ", stale)}.");
    }

    /// <summary>An exclusion can only stay honest while its package exists; one left behind by a
    /// removed dependency would silently keep a real gap open later under the same name.</summary>
    [Fact]
    public void EveryRecordedExclusionStillNamesAReferencedPackage()
    {
        var referenced = PackageReferences();
        var orphaned   = NotCredited.Keys
                         .Where(k => !referenced.Contains(k, StringComparer.OrdinalIgnoreCase))
                         .ToArray();

        Assert.True(orphaned.Length == 0,
            $"NotCredited names packages the csproj no longer references: {string.Join(", ", orphaned)}.");
    }

    /// <summary>
    /// The third list. The README table is what a reader outside the app sees, and it drifted the same
    /// way the credits did. Matched on the package name alone, so ordinary edits to a row's purpose,
    /// version or link do not break this.
    /// </summary>
    [Fact]
    public void EveryPackageReferenceAppearsInTheReadmeTable()
    {
        var readme  = ReadRepoFile("README.md");
        var missing = PackageReferences()
                      .Where(p => !readme.Contains($"[{p}](", StringComparison.Ordinal))
                      .ToArray();

        Assert.True(missing.Length == 0,
            $"Referenced but absent from the README \"External libraries\" table: {string.Join(", ", missing)}.");
    }
}
