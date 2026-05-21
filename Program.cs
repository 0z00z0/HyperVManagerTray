using HyperVNetworkSwitcher;
using Microsoft.Extensions.Logging;

// Catch any unhandled exception on the UI thread and show it before the process dies.
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, args) =>
    MessageBox.Show(args.Exception.ToString(), "HyperVNetworkSwitcher — Startup Error",
        MessageBoxButtons.OK, MessageBoxIcon.Error);

// Also catch exceptions on background threads.
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    MessageBox.Show(args.ExceptionObject?.ToString() ?? "Unknown error",
        "HyperVNetworkSwitcher — Fatal Error",
        MessageBoxButtons.OK, MessageBoxIcon.Error);

Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

try
{
    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HyperVNetworkSwitcher");
    Directory.CreateDirectory(logDir);
    var logFile = Path.Combine(logDir, "switcher.log");

    using var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Information);
        b.AddSimpleFileLogger(logFile);
    });

    var configPath = ConfigManager.GetConfigPath();
    if (!File.Exists(configPath))
    {
        MessageBox.Show(
            $"config.json not found at:\n{configPath}\n\nPlace config.json next to the executable and restart.",
            "HyperVNetworkSwitcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        return;
    }

    using var configManager  = new ConfigManager(configPath, loggerFactory.CreateLogger<ConfigManager>());
    using var hyperVManager  = new HyperVManager(loggerFactory.CreateLogger<HyperVManager>());
    using var networkMonitor = new NetworkMonitor(configManager, hyperVManager, loggerFactory.CreateLogger<NetworkMonitor>());
    using var tray           = new TrayApplication(configManager, networkMonitor, hyperVManager, loggerFactory.CreateLogger<TrayApplication>());

    networkMonitor.Start();
    Application.Run(tray);
}
catch (Exception ex)
{
    MessageBox.Show(
        $"Failed to start HyperVNetworkSwitcher:\n\n{ex}",
        "HyperVNetworkSwitcher — Startup Error",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}
