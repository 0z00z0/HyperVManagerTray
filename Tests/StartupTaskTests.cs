using HyperVManagerTray.Helpers;
using Microsoft.Win32.TaskScheduler;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Issue #61: the app stopped starting at logon. The task existed and the trigger fired, but a bare
/// <c>schtasks /Create</c> inherits Task Scheduler's defaults — <c>DisallowStartIfOnBatteries=true</c>
/// and <c>StopIfGoingOnBatteries=true</c> — so a laptop that boots on battery never gets the app, and
/// <c>schtasks /Run</c> still exits 0. Both flags are asserted here on both paths that can leave a
/// task behind: fresh registration, and the self-heal that repairs an already-registered one.
///
/// <para>Nothing here registers anything. <see cref="TaskService.NewTask"/> is an in-memory COM
/// object, and the repair's scheduler calls arrive as delegates — the machine's real tasks are
/// untouched.</para>
/// </summary>
public class StartupTaskTests
{
    private const string Exe = @"C:\Users\Someone\AppData\Local\Programs\HyperVManagerTray\HyperVManagerTray.exe";
    private static readonly TaskIdentity User = new("S-1-5-21-1-2-3-1001", @"AzureAD\SomeUser");

    // ── Part 1: registration ─────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this issue. A definition that "forgets" either flag is a definition that never
    /// starts the app on a machine booted on battery.
    /// </summary>
    [Fact]
    public void RegisteredTask_ClearsBothBatteryFlags()
    {
        using var ts = new TaskService();
        using TaskDefinition td = StartupTaskDefinition.BuildLogonTask(ts, Exe, User);

        Assert.False(td.Settings.DisallowStartIfOnBatteries);
        Assert.False(td.Settings.StopIfGoingOnBatteries);
    }

    /// <summary>
    /// The reason the two lines above cannot be dropped as "surely the default": a virgin definition
    /// carries both flags set. This is the bug, reproduced on the same object the app registers.
    /// </summary>
    [Fact]
    public void AVirginDefinition_CarriesTheHostileDefaults()
    {
        using var ts = new TaskService();
        using TaskDefinition td = ts.NewTask();

        Assert.True(td.Settings.DisallowStartIfOnBatteries);
        Assert.True(td.Settings.StopIfGoingOnBatteries);
    }

    /// <summary>The rest of what makes the task work at all: a logon trigger for this user, the exe
    /// (quoted, for install paths with a space) and the elevation a requireAdministrator app needs to
    /// start without a UAC prompt.</summary>
    [Fact]
    public void RegisteredTask_RunsThisExeElevatedAtLogon()
    {
        using var ts = new TaskService();
        using TaskDefinition td = StartupTaskDefinition.BuildLogonTask(ts, Exe, User);

        Assert.Equal(User.Name, td.Triggers.OfType<LogonTrigger>().Single().UserId);
        Assert.Equal($"\"{Exe}\"", td.Actions.OfType<ExecAction>().Single().Path);

        Assert.Equal(User.Sid, td.Principal.UserId);
        Assert.Equal(TaskLogonType.InteractiveToken, td.Principal.LogonType);
        Assert.Equal(TaskRunLevel.Highest, td.Principal.RunLevel);
    }

    /// <summary>The installer registers the same task by name (its own <c>TaskName</c> define), so the
    /// tray toggle and the setup option must not drift into two tasks.</summary>
    [Fact]
    public void TaskPath_IsTheInstallersTaskInTheRootFolder()
    {
        Assert.Equal("HyperVManagerTray", StartupTaskDefinition.TaskName);
        Assert.Equal(@"\HyperVManagerTray", StartupTaskDefinition.TaskPath);
    }

    // ── Part 2: the self-heal decision ───────────────────────────────────────────

    /// <summary>Either flag on its own blocks the app, so either is enough to trigger a repair.</summary>
    [Theory]
    [InlineData(true,  true,  true)]
    [InlineData(true,  false, true)]
    [InlineData(false, true,  true)]
    [InlineData(false, false, false)]
    public void PowerFlags_NeedRepairWheneverEitherIsSet(bool disallowStart, bool stopOnBattery, bool expected)
        => Assert.Equal(expected, new StartupTaskPowerFlags(disallowStart, stopOnBattery).NeedsRepair);

    /// <summary>A task carrying the scheduler defaults — i.e. every task registered before this
    /// build — is detected and rewritten.</summary>
    [Fact]
    public void ATaskWithTheBatteryDefaults_IsRepaired()
    {
        var scheduler = new FakeScheduler(new StartupTaskPowerFlags(true, true));

        Assert.Equal(StartupTaskRepairOutcome.Repaired, scheduler.RunRepair());
        Assert.Equal(1, scheduler.Repairs);
    }

    /// <summary>
    /// The idempotence guard. This runs at every app start, so a task that is already power-safe must
    /// cost NO write: rewriting one on every launch would churn the scheduler forever.
    /// </summary>
    [Fact]
    public void AnAlreadyPowerSafeTask_IsLeftAloneWithNoWrite()
    {
        var scheduler = new FakeScheduler(new StartupTaskPowerFlags(false, false));

        Assert.Equal(StartupTaskRepairOutcome.AlreadyPowerSafe, scheduler.RunRepair());
        Assert.Equal(0, scheduler.Repairs);
    }

    /// <summary>No task means the user never enabled startup. Not an error, and emphatically not a
    /// reason to create one — that stays the user's choice.</summary>
    [Fact]
    public void AMissingTask_IsNotAnErrorAndCreatesNothing()
    {
        var scheduler = new FakeScheduler(flags: null);

        Assert.Equal(StartupTaskRepairOutcome.NotRegistered, scheduler.RunRepair());
        Assert.Equal(0, scheduler.Repairs);
    }

    /// <summary>A failed rewrite (no rights, a task locked by the scheduler) costs the repair, never
    /// the app's startup — this runs inside OnLaunched's try block.</summary>
    [Fact]
    public void AFailedRepair_IsReportedAndNeverThrows()
    {
        Exception? reported = null;

        var outcome = StartupTaskRepair.Run(
            () => new StartupTaskPowerFlags(true, false),
            () => throw new UnauthorizedAccessException("access denied"),
            ex => reported = ex);

        Assert.Equal(StartupTaskRepairOutcome.Failed, outcome);
        Assert.IsType<UnauthorizedAccessException>(reported);
    }

    /// <summary>The same holds for the read: an unreachable Task Scheduler service must not take the
    /// app down on the way past.</summary>
    [Fact]
    public void AFailedRead_IsReportedAndNeverThrows()
    {
        Exception? reported = null;
        bool       repaired = false;

        var outcome = StartupTaskRepair.Run(
            () => throw new InvalidOperationException("scheduler unavailable"),
            () => repaired = true,
            ex => reported = ex);

        Assert.Equal(StartupTaskRepairOutcome.Failed, outcome);
        Assert.IsType<InvalidOperationException>(reported);
        Assert.False(repaired);
    }

    /// <summary>Repairing what the repair itself wrote must be a no-op — the self-heal runs on every
    /// start, so a second pass over a task it just fixed may not write again.</summary>
    [Fact]
    public void RepairingTwice_WritesOnlyOnce()
    {
        var scheduler = new FakeScheduler(new StartupTaskPowerFlags(true, true));

        Assert.Equal(StartupTaskRepairOutcome.Repaired, scheduler.RunRepair());
        Assert.Equal(StartupTaskRepairOutcome.AlreadyPowerSafe, scheduler.RunRepair());
        Assert.Equal(1, scheduler.Repairs);
    }

    // ── Part 3: the repair actually clears the flags ─────────────────────────────

    /// <summary>
    /// What the repair writes, asserted on real <c>TaskSettings</c> rather than on the fake: the
    /// self-heal calls exactly this on the registered task's definition before RegisterChanges.
    /// </summary>
    [Fact]
    public void ApplyPowerSafe_ClearsBothFlagsOnARegisteredTasksSettings()
    {
        using var ts = new TaskService();
        using TaskDefinition existing = ts.NewTask();   // carries the defaults, like the live task did

        StartupTaskDefinition.ApplyPowerSafe(existing.Settings);

        Assert.Equal(new StartupTaskPowerFlags(false, false),
                     StartupTaskDefinition.ReadPowerFlags(existing.Settings));
    }

    /// <summary>Stands in for the registered task the self-heal reads and rewrites: it holds the
    /// battery settings, applies the repair to them, and counts the writes so "no write" is
    /// assertable rather than assumed.</summary>
    private sealed class FakeScheduler(StartupTaskPowerFlags? flags)
    {
        private StartupTaskPowerFlags? _flags = flags;

        public int Repairs { get; private set; }

        public StartupTaskRepairOutcome RunRepair() => StartupTaskRepair.Run(
            () => _flags,
            () =>
            {
                Repairs++;
                _flags = new StartupTaskPowerFlags(false, false);
            });
    }
}
