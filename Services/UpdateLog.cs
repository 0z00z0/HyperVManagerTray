using Microsoft.Extensions.Logging;
using ZeroZero.Primitives;

namespace HyperVManagerTray.Services;

/// <summary>
/// The update component's two-member log sink over this app's own logger. The component owns no
/// logging framework and takes <see cref="ILogSink"/>; everything it writes lands in the category
/// this is built with, which is what puts the verification verdict for a refused installer in the
/// same log as the check that found the release.
/// </summary>
public sealed class UpdateLog(ILogger logger) : ILogSink
{
    public void Info(string message) => logger.LogInformation("{Message}", message);

    public void Error(string source, Exception? ex) =>
        logger.LogError(ex, "Update failure in {Source}", source);
}
