using System.Text;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Runs Hyper-V PowerShell cmdlets through a single persistent powershell.exe worker
/// process (read-eval loop over stdin/stdout), avoiding the 1-2 s process+module startup
/// that a per-command spawn would cost on every call.  The worker is reaped after a couple
/// of minutes of inactivity so an idle tray app holds no extra process.
///
/// An out-of-process worker (rather than the Microsoft.PowerShell.SDK in-process runspace)
/// is used because the SDK fails to initialise in self-contained builds due to a registry
/// lookup (PSSnapInReader) that returns null when the Windows PowerShell engine key is
/// absent.  powershell.exe (Windows PowerShell 5.1) is always present on Windows 10/11 and
/// supports all required Hyper-V cmdlets.  Commands are passed as Base64-encoded Unicode
/// lines to sidestep all quoting/escaping concerns.
///
/// Phase 1 scope: this class now owns ONLY switch binding and host-vNIC repair — safety-critical
/// host-networking operations kept on PowerShell deliberately. VM status/metrics/power/IPs moved
/// to <see cref="VmService"/> (native WMI, event-driven, no polling). See <c>VmService</c>'s
/// class doc for the full migration rationale.
/// </summary>
public sealed class HyperVManager : IDisposable
{
    private readonly ILogger<HyperVManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);  // serialise concurrent calls

    public HyperVManager(ILogger<HyperVManager> logger)
    {
        _logger = logger;
        // Periodically stop the worker process when it has been idle long enough that the
        // next caller is better served by a fresh spawn than by ~80 MB of warm-but-unused PS.
        _workerReaper = new System.Threading.Timer(
            _ => ReapIdleWorker(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects a VM's NIC to the given virtual switch, but only if it isn't already there.
    /// <c>Connect-VMNetworkAdapter</c> re-applies the binding and briefly bounces the VM's
    /// network even when the switch is unchanged, so we check first to avoid a needless blip
    /// (e.g. on every app launch, where the in-session guards start empty).
    /// </summary>
    public async Task ApplySwitchAsync(string vmName, string nicName, string switchName)
    {
        var vm  = Esc(vmName);
        var nic = Esc(nicName);
        var sw  = Esc(switchName);
        var (ok, output) = await RunAsync(
            $"if ((Get-VMNetworkAdapter -VMName '{vm}' -Name '{nic}').SwitchName -eq '{sw}') {{ 'SKIP' }} " +
            $"else {{ Connect-VMNetworkAdapter -VMName '{vm}' -Name '{nic}' -SwitchName '{sw}'; 'CONNECTED' }}");

        if (!ok)                          _logger.LogError("ApplySwitchAsync error: {Error}", output);
        else if (output.Contains("SKIP")) _logger.LogInformation("VM {Vm} already on '{Switch}' — no reconnect", vmName, switchName);
        else                              _logger.LogInformation("Switch applied: {Vm} → {Switch}", vmName, switchName);
    }

    // Re-homing an external switch's adapter is slow (~25 s observed), so this gets a longer
    // timeout than the default. The bind is now done as a single atomic Set-VMSwitch call that
    // never disables host sharing, so even a hard kill mid-sequence cannot leave the host
    // without a management vNIC (the failure mode that previously disconnected the host).
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Binds a Hyper-V virtual switch to a physical NIC (makes it External, with the host
    /// sharing the adapter) — but only when it isn't already in that exact state.
    ///
    /// <para><b>Crash/kill safety.</b> The rebind is a single atomic <c>Set-VMSwitch
    /// -NetAdapterName … -AllowManagementOS $true</c>. Host sharing is <em>never</em> disabled,
    /// so there is no window in which a hard kill (process crash, timeout kill) could leave the
    /// host adapter with no management vNIC and therefore no IP — the failure that previously
    /// disconnected the host when the app crashed between a <c>$false</c> and <c>$true</c> toggle.</para>
    ///
    /// <para><b>No-op fast path.</b> If the switch is already External, sharing with the management
    /// OS, and bound to the target adapter, nothing is changed — this stops host-network flicker
    /// on every launch / redundant evaluation.</para>
    ///
    /// <para><b>Self-heal.</b> Any duplicate/orphaned <c>vEthernet (&lt;switch&gt;)</c> management
    /// vNICs left behind by older builds are collapsed back to a single one (without toggling
    /// sharing, so no connectivity blip).</para>
    ///
    /// <para>If the target adapter isn't present (e.g. the USB NIC is unplugged / on a different
    /// network), the switch is left untouched.</para>
    /// </summary>
    public async Task UpdateSwitchBindingAsync(string switchName, string adapterName)
    {
        var sw  = Esc(switchName);
        var nic = Esc(adapterName);
        var script =
            $"$want = (Get-NetAdapter -Name '{nic}' -ErrorAction SilentlyContinue).InterfaceDescription; " +
            $"$s = Get-VMSwitch -Name '{sw}' -ErrorAction SilentlyContinue; " +
             "if (-not $want) { 'NOADAPTER' } elseif (-not $s) { 'NOSWITCH' } " +
             "elseif ($s.SwitchType -eq 'External' -and $s.AllowManagementOS -and " +
             "$s.NetAdapterInterfaceDescription -eq $want) { 'SKIP' } else { " +
            $"Set-VMSwitch -Name '{sw}' -NetAdapterName '{nic}' -AllowManagementOS $true; 'BOUND' }}";

        var (ok, output) = await RunAsync(script, BindTimeout);

        if (!ok)                                _logger.LogError("UpdateSwitchBindingAsync error: {Error}", output);
        else if (output.Contains("NOADAPTER"))  _logger.LogInformation("Adapter '{Adapter}' not present — switch '{Switch}' left unchanged", adapterName, switchName);
        else if (output.Contains("NOSWITCH"))   _logger.LogWarning("Virtual switch '{Switch}' not found — cannot bind", switchName);
        else if (output.Contains("SKIP"))       _logger.LogInformation("Switch '{Switch}' already bound to '{Adapter}' — no rebind", switchName, adapterName);
        else
        {
            _logger.LogInformation("Switch '{Switch}' bound to '{Adapter}'", switchName, adapterName);
            // A rebind (especially across a dock undock/redock) can leave Hyper-V with a duplicate
            // host vNIC sharing one MAC, which kills the HOST's connectivity while the VM stays
            // fine. Collapse it right here so the broken state never persists. No-op when healthy.
            await RepairHostVNicAsync(switchName);
        }
    }

    /// <summary>Outcome of <see cref="RepairHostVNicAsync"/>.</summary>
    public enum HostVNicState { Ok, Repaired, Reshared, NoSwitch, Error }

    /// <summary>
    /// Ensures a switch has exactly ONE host (management-OS) vNIC, repairing the failure mode where
    /// the host loses its own network while the VM stays connected.
    ///
    /// <para>After a rebind — typically a dock undock/redock — Hyper-V can leave the switch with a
    /// DUPLICATE host vNIC, and the duplicate carries the SAME MAC as the original. Two host vNICs
    /// sharing a MAC break the host's egress (the VM, with its own MAC, is unaffected). The earlier
    /// "only clean when no host vNIC is Up" guard never fired here, because the dead duplicate is Up
    /// (APIPA). So we act on the unambiguous signal — host vNIC count &gt; 1 — and collapse to a
    /// single fresh vNIC via an <c>AllowManagementOS $false→$true</c> reset (a ~1.5 s host blip; the
    /// VM keeps its connection). This is exactly the manual recovery that resolves it.</para>
    ///
    /// <para>Also self-heals an interrupted reset: if the switch is External but sharing was left
    /// off (count 0), it re-enables sharing. No-op (and no blip) when already healthy. Safe to call
    /// after every bind, on startup, or on demand.</para>
    /// </summary>
    public async Task<HostVNicState> RepairHostVNicAsync(string switchName)
    {
        var sw = Esc(switchName);
        var script =
            $"$s = Get-VMSwitch -Name '{sw}' -ErrorAction SilentlyContinue; " +
             "if (-not $s) { 'NOSWITCH' } else { " +
            $"$m = @(Get-VMNetworkAdapter -ManagementOS -SwitchName '{sw}' -ErrorAction SilentlyContinue); " +
             "if ($m.Count -gt 1) { " +
            $"Set-VMSwitch -Name '{sw}' -AllowManagementOS $false; " +
             "Start-Sleep -Milliseconds 1500; " +
            $"Set-VMSwitch -Name '{sw}' -AllowManagementOS $true; " +
             "'REPAIRED ' + $m.Count " +
             "} elseif ($s.SwitchType -eq 'External' -and -not $s.AllowManagementOS) { " +
            $"Set-VMSwitch -Name '{sw}' -AllowManagementOS $true; 'RESHARED' " +
             "} else { 'OK' } }";

        var (ok, output) = await RunAsync(script, BindTimeout);

        if (!ok)
        {
            _logger.LogWarning("RepairHostVNicAsync('{Switch}') error: {Error}", switchName, output);
            return HostVNicState.Error;
        }
        if (output.Contains("REPAIRED"))
        {
            _logger.LogWarning("Collapsed duplicate host vNIC(s) on switch '{Switch}' to one ({Detail})", switchName, output.Trim());
            return HostVNicState.Repaired;
        }
        if (output.Contains("RESHARED"))
        {
            _logger.LogInformation("Restored host sharing on switch '{Switch}'", switchName);
            return HostVNicState.Reshared;
        }
        if (output.Contains("NOSWITCH")) return HostVNicState.NoSwitch;
        return HostVNicState.Ok;   // already healthy
    }

    // ── Persistent PowerShell worker ────────────────────────────────────────────
    //
    // Spawning a fresh powershell.exe per command costs 1-2 s of startup + Hyper-V module
    // load each time. Instead, one hidden worker process runs a read-eval loop: each command
    // is sent as a Base64 line on stdin, executed in the warm session, and the output is
    // terminated by an OK/ERR sentinel line. Commands keep the same semantics as a standalone
    // -EncodedCommand invocation (non-terminating errors are merged into output; terminating
    // errors yield ok=false).
    //
    // Lifecycle: spawned lazily on first use, killed+respawned on timeout or crash, and
    // reaped after WorkerIdleTimeout of inactivity so an idle tray app holds no extra
    // process (~80 MB) — process startup is only paid again on the next burst of activity.

    private const string SentinelOk  = "<<HVMT:OK>>";
    private const string SentinelErr = "<<HVMT:ERR>>";
    private static readonly TimeSpan WorkerIdleTimeout = TimeSpan.FromMinutes(2);

    private System.Diagnostics.Process? _worker;
    private StreamWriter? _workerIn;
    private StreamReader? _workerOut;
    private DateTime _workerLastUseUtc;
    private readonly System.Threading.Timer _workerReaper;

    // The worker's read-eval loop. $ProgressPreference is session-wide; each command runs
    // in a fresh scriptblock with default (Continue) error semantics, mirroring how the
    // scripts behaved as standalone -EncodedCommand invocations.
    private const string WorkerBootstrap =
        "$ProgressPreference='SilentlyContinue'; " +
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "while ($true) { " +
        "$l = [Console]::In.ReadLine(); " +
        "if ($null -eq $l) { break }; " +
        "if ($l.Length -eq 0) { continue }; " +
        "$e = $false; " +
        "try { " +
        "$s = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($l)); " +
        "$o = & ([ScriptBlock]::Create($s)) 2>&1 | Out-String; " +
        "if ($o.Length -gt 0) { [Console]::Out.Write($o); if (-not $o.EndsWith(\"`n\")) { [Console]::Out.WriteLine() } } " +
        "} catch { [Console]::Out.WriteLine($_.Exception.Message); $e = $true }; " +
        "if ($e) { [Console]::Out.WriteLine('" + SentinelErr + "') } else { [Console]::Out.WriteLine('" + SentinelOk + "') }; " +
        "[Console]::Out.Flush() }";

    private void EnsureWorker()
    {
        if (_worker is { HasExited: false }) return;

        KillWorker(); // clean up any dead remnants

        var bootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(WorkerBootstrap));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -EncodedCommand {bootstrap}",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        _worker = System.Diagnostics.Process.Start(psi)
                  ?? throw new InvalidOperationException("Failed to start PowerShell worker");
        _workerIn  = _worker.StandardInput;
        _workerIn.AutoFlush = true;
        _workerOut = _worker.StandardOutput;

        // Drain stderr in the background so a full pipe can never block the worker.
        var stderr = _worker.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) is not null)
                    _logger.LogDebug("PS worker stderr: {Line}", line);
            }
            catch { /* worker exited — expected */ }
        });

        _logger.LogInformation("PowerShell worker started (pid {Pid}).", _worker.Id);
    }

    private void KillWorker()
    {
        var w = _worker;
        _worker = null;
        if (w is null) return;
        try { if (!w.HasExited) w.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { w.Dispose(); } catch { }
        _workerIn  = null;
        _workerOut = null;
    }

    /// <summary>Reaper callback: kills the worker after a period of inactivity (never blocks).</summary>
    private void ReapIdleWorker()
    {
        try
        {
            if (_worker is null || DateTime.UtcNow - _workerLastUseUtc < WorkerIdleTimeout) return;
            if (!_lock.Wait(0)) return;   // a command is running — check again next tick
            try
            {
                if (_worker is not null && DateTime.UtcNow - _workerLastUseUtc >= WorkerIdleTimeout)
                {
                    _logger.LogDebug("PowerShell worker idle for {Idle} — stopping.", WorkerIdleTimeout);
                    KillWorker();
                }
            }
            finally { _lock.Release(); }
        }
        catch (ObjectDisposedException) { /* raced with Dispose — nothing to do */ }
    }

    private async Task<(bool ok, string output)> RunAsync(string psScript, TimeSpan? timeout = null)
    {
        await _lock.WaitAsync();
        try
        {
            var to = timeout ?? TimeSpan.FromSeconds(30);
            _logger.LogDebug("PS> {Script}", psScript);
            _workerLastUseUtc = DateTime.UtcNow;

            // One retry: if the worker died between commands (or was idle-reaped mid-write),
            // respawn and resend. A timeout does NOT retry — the command may be genuinely hung.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    EnsureWorker();
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
                    await _workerIn!.WriteLineAsync(encoded);

                    var sb = new StringBuilder();
                    using var cts = new CancellationTokenSource(to);
                    while (true)
                    {
                        var line = await _workerOut!.ReadLineAsync(cts.Token);
                        if (line is null) throw new EndOfStreamException("PowerShell worker exited unexpectedly");
                        if (line == SentinelOk)  { _workerLastUseUtc = DateTime.UtcNow; return (true,  sb.ToString().Trim()); }
                        if (line == SentinelErr) { _workerLastUseUtc = DateTime.UtcNow; return (false, sb.ToString().Trim()); }
                        sb.AppendLine(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Hung cmdlet (e.g. WMI/DCOM lookup that never returns) — kill the whole
                    // worker; the next command starts a fresh one.
                    _logger.LogWarning("PowerShell command timed out after {Timeout}s: {Script}", to.TotalSeconds, psScript);
                    KillWorker();
                    return (false, $"Timed out after {to.TotalSeconds:0} s");
                }
                catch (Exception ex) when (attempt == 0)
                {
                    _logger.LogDebug(ex, "PowerShell worker unavailable — restarting once.");
                    KillWorker();
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Escapes a value for use inside a PowerShell single-quoted string.</summary>
    private static string Esc(string s) => s.Replace("'", "''");

    public void Dispose()
    {
        _workerReaper.Dispose();
        KillWorker();
        _lock.Dispose();
    }
}
