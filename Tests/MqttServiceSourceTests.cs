using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>Guards three threading facts about the live MQTT service, in the same coarse way as
/// <see cref="VmConnectFlowSourceTests"/>: <c>Services\MqttService.cs</c> is not linked into this
/// assembly, and none of the three is observable from a unit test even if it were. They read text, so
/// they cannot see semantics.</summary>
public class MqttServiceSourceTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!;

    // Located from THIS file's compile-time path: the sources are not copied to the test output.
    private static string Source(params string[] parts)
    {
        var path = Path.Combine([RepoRoot(), .. parts]);
        Assert.True(File.Exists(path), $"'{path}' not found — fix this test's path, don't skip it.");
        return File.ReadAllText(path);
    }

    /// <summary>The body of one method, by brace matching from its signature.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found — the method was renamed; update this test.");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{signature}'.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        Assert.Fail($"unbalanced braces after '{signature}'.");
        return string.Empty;
    }

    /// <summary><c>MqttConnection.Dispose</c> blocks for up to three seconds, and <c>_lock</c> is taken
    /// on WMI watcher threads and on the UI thread — so a teardown under it freezes the Settings
    /// window. The retired connection is taken out under the lock and disposed outside it.</summary>
    [Fact]
    public void ARetiredConnectionIsDisposedOffTheLock()
    {
        var src = Source("Services", "MqttService.cs");

        Assert.DoesNotContain("Dispose", Body(src, "private void Recreate("), StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose", Body(src, "private MqttConnection? TakeRetired("),
                              StringComparison.Ordinal);
        Assert.Contains("Retire(TakeRetired(", src, StringComparison.Ordinal);
    }

    /// <summary>A reconcile awaits the abandoned-identity clear before it applies anything, so without
    /// the gate and the ticket an older snapshot can land last and roll newer settings back.</summary>
    [Fact]
    public void ReconcilesAreSerialisedAndTheStaleOneStandsDown()
    {
        var body = Body(Source("Services", "MqttService.cs"), "private async Task ReconcileAsync(");

        Assert.Contains("_reconcileGate.WaitAsync()", body, StringComparison.Ordinal);
        Assert.Contains("_reconcileGate.Release()", body, StringComparison.Ordinal);
        Assert.Contains("MqttReconcile.Superseded(", body, StringComparison.Ordinal);
    }

    /// <summary>The exit runs on the UI thread. Detaching must stay synchronous — the services are
    /// disposed immediately after — but the teardown behind it must not, or the exit freezes for three
    /// seconds. It is still joined, and before the logger factory goes, because it logs.</summary>
    [Fact]
    public void TheExitStartsTheBrokerTeardownWithoutBlockingOnIt()
    {
        var body = Body(Source("App.xaml.cs"), "private void OnExit()");

        Assert.DoesNotContain("_mqtt?.Dispose()", body, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"_mqtt\?\.BeginDisposeAsync\(\)"), body);

        int started = body.IndexOf("BeginDisposeAsync()", StringComparison.Ordinal);
        int joined  = body.IndexOf("mqttTeardown?.Wait(", StringComparison.Ordinal);
        int logger  = body.IndexOf("_loggerFactory?.Dispose()", StringComparison.Ordinal);

        Assert.True(joined > started, "the teardown must be joined after it is started.");
        Assert.True(logger > joined, "the teardown must be joined BEFORE the logger factory is disposed.");
    }
}
