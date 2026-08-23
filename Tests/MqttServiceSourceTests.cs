using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Guards three wiring facts about the live MQTT service that no other test can reach, in the same
/// coarse way and for the same reason as <see cref="VmConnectFlowSourceTests"/>:
/// <c>Services\MqttService.cs</c> holds the broker connection and the app's services, so it is
/// deliberately not linked into this assembly. <see cref="MqttReconcileTests"/> proves the decisions it
/// takes are right; nothing there notices if the call site stops honouring them.
///
/// <para>All three are threading facts — where a blocking teardown runs, and what serialises a
/// reconcile — which a unit test could not observe even with the service linked. They read text, so
/// they cannot see semantics; they are aimed at the realistic regression during an unrelated edit.</para>
/// </summary>
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

    /// <summary>
    /// <c>MqttConnection.Dispose</c> blocks for up to three seconds (it publishes the retained offline
    /// state and waits for its command worker). <c>_lock</c> is taken by the event pump on WMI watcher
    /// threads and by <c>IsConnected</c>/<c>Activity</c> on the UI thread, so tearing a connection down
    /// under it freezes the Settings window for that long — reliably, when the retiring connection is
    /// running a command that itself calls back into the pump. The retired connection is taken out
    /// under the lock and disposed outside it.
    /// </summary>
    [Fact]
    public void ARetiredConnectionIsDisposedOffTheLock()
    {
        var src = Source("Services", "MqttService.cs");

        Assert.DoesNotContain("Dispose", Body(src, "private void Recreate("), StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose", Body(src, "private MqttConnection? TakeRetired("),
                              StringComparison.Ordinal);
        Assert.Contains("Retire(TakeRetired(", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two reloads in quick succession each schedule a reconcile carrying its own config snapshot, and
    /// a reconcile awaits the abandoned-identity clear before it applies anything. Without the gate and
    /// the ticket the older one can land last and write the user's newer settings back out.
    /// </summary>
    [Fact]
    public void ReconcilesAreSerialisedAndTheStaleOneStandsDown()
    {
        var body = Body(Source("Services", "MqttService.cs"), "private async Task ReconcileAsync(");

        Assert.Contains("_reconcileGate.WaitAsync()", body, StringComparison.Ordinal);
        Assert.Contains("_reconcileGate.Release()", body, StringComparison.Ordinal);
        Assert.Contains("MqttReconcile.Superseded(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit runs on the UI thread. Detaching from VmService/NetworkMonitor must stay synchronous —
    /// those are disposed immediately after — but the connection teardown behind it must not, or a
    /// user-initiated exit freezes for up to three seconds. It is still joined, and before the logger
    /// factory goes, because it logs: a lost teardown leaves the device "available" in Home Assistant
    /// until the broker's keep-alive expires.
    /// </summary>
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
