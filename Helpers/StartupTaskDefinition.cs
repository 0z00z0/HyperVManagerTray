using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// The two forms of the current user's identity a task definition needs. They are not
/// interchangeable: <see cref="TaskPrincipal.UserId"/> stores the SID verbatim, while a trigger
/// stores the resolved account NAME (the scheduler rewrites a SID into one anyway).
/// </summary>
internal readonly record struct TaskIdentity(string Sid, string Name)
{
    internal static TaskIdentity? Current()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        string? sid = me.User?.Value;
        return string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(me.Name) ? null : new(sid, me.Name);
    }
}

/// <summary>
/// The single definition of the "run at logon" task — what it looks like, and the power settings
/// every writer of it must carry (issue #61).
///
/// <para>Task Scheduler's DEFAULTS include <c>DisallowStartIfOnBatteries=true</c> and
/// <c>StopIfGoingOnBatteries=true</c>, which a bare <c>schtasks /Create</c> inherits. On a laptop
/// that boots or logs on while on battery the scheduler then accepts the trigger and never starts
/// the app — and <c>schtasks /Run</c> still exits 0, so nothing surfaces it. Both flags must be
/// cleared by every writer: this class, the installer's RegisterStartupTask, and the startup
/// self-heal that repairs tasks registered by older builds.</para>
/// </summary>
internal static class StartupTaskDefinition
{
    /// <summary>Also hard-coded as <c>TaskName</c> in installer\HyperVManagerTray.iss, so the
    /// installer option and the in-app toggle control the same task.</summary>
    internal const string TaskName = "HyperVManagerTray";

    /// <summary>The task lives in the root folder, so its path is just a separator plus the name.</summary>
    internal static string TaskPath => @"\" + TaskName;

    internal static string Description =>
        $"Starts {AppInfo.Name} at logon, elevated, with power-safe settings.";

    /// <summary>Builds (never registers) the logon task for <paramref name="exePath"/>.</summary>
    internal static TaskDefinition BuildLogonTask(TaskService ts, string exePath, TaskIdentity user)
    {
        TaskDefinition td = ts.NewTask();
        td.RegistrationInfo.URI         = TaskPath;
        td.RegistrationInfo.Description = Description;
        td.Triggers.Add(new LogonTrigger { UserId = user.Name });
        // Quoted: the install path can contain a space, and this is the form the installer's
        // schtasks /TR has always written.
        td.Actions.Add(new ExecAction($"\"{exePath}\""));

        td.Principal.UserId    = user.Sid;
        td.Principal.LogonType = TaskLogonType.InteractiveToken;   // interactive session ⇒ the tray icon appears
        td.Principal.RunLevel  = TaskRunLevel.Highest;             // requireAdministrator app, no logon UAC prompt

        ApplyPowerSafe(td.Settings);
        // Explicit, not left to the library default (issue #71): Enable() re-registers with this
        // definition via CreateOrUpdate, so switching the toggle on must also clear a previous
        // disable, not merely leave it to chance.
        td.Settings.Enabled = true;
        return td;
    }

    /// <summary>Clears the two scheduler defaults that stop the app ever starting on battery.</summary>
    internal static void ApplyPowerSafe(TaskSettings settings)
    {
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries     = false;
    }

    /// <summary>The battery settings of a registered task, in the form the self-heal decides on.</summary>
    internal static StartupTaskPowerFlags ReadPowerFlags(TaskSettings settings) =>
        new(settings.DisallowStartIfOnBatteries, settings.StopIfGoingOnBatteries);
}
