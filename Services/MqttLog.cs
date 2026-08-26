using Microsoft.Extensions.Logging;
using ZeroZero.Mqtt;

namespace HyperVManagerTray.Services;

/// <summary>
/// The module's two-member log sink over this app's own logger (issue #75). The module owns no logging
/// framework and takes <see cref="IMqttLog"/>; everything it writes lands in the <c>mqtt</c> category,
/// which <c>App</c> routes to mqtt.log.
/// </summary>
/// <remarks>The module sanitises an exception to its type and message before handing it over, so no
/// staged credential reaches this sink.</remarks>
public sealed class MqttLog(ILogger logger) : IMqttLog
{
    public void Info(string message) => logger.LogInformation("{Message}", message);

    public void Error(string source, Exception? ex) =>
        logger.LogError(ex, "MQTT failure in {Source}", source);
}
