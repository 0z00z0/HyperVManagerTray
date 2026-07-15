using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVManagerTray.UI;

/// <summary>
/// Process-wide gateway to the "ui" category logger → ui.log (issue #21): tray-menu command
/// invocations, window open/close, dashboard power-button clicks, and rename-flow steps.
///
/// <para>A static gateway rather than constructor injection because the UI emitters don't share one
/// owner: <see cref="TrayMenu"/> and <see cref="DashboardWindow"/> are built by <c>App</c>, but
/// <see cref="AdapterRenameFlow"/> is built by <c>SettingsWindow</c>, so there is no single seam to
/// thread an <see cref="ILogger"/> through them all. This mirrors the codebase's existing static
/// logging helper <c>AppInfo.AppendCrashLogLine</c>. <c>App</c> assigns <see cref="Logger"/> once at
/// startup (before any UI exists); until then it is a no-op <see cref="NullLogger"/>, so callers never
/// need a null check. Log identifiers only — never PII or secrets.</para>
/// </summary>
internal static class UiActivityLog
{
    /// <summary>The "ui" category logger. Set once by <c>App</c> at startup; a no-op until then.</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;
}
