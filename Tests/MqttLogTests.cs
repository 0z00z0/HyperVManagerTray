using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// The module's two-member log sink over this app's own logger (issue #75). Thin, but not free: the
/// module has no logging framework of its own, so anything it fails to hand over here is a failure the
/// app never records — and mqtt.log is the only account of a connection nobody was watching.
/// </summary>
public class MqttLogTests
{
    private sealed record Line(LogLevel Level, string Message, Exception? Exception);

    private sealed class Recorder : ILogger
    {
        public readonly List<Line> Lines = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
            => Lines.Add(new Line(logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public void Info_WritesTheMessageAtInformation()
    {
        var recorder = new Recorder();

        new MqttLog(recorder).Info("Connected to broker.lan:8883 over TCP.");

        var line = Assert.Single(recorder.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Connected to broker.lan:8883 over TCP.", line.Message);
        Assert.Null(line.Exception);
    }

    /// <summary>The exception is carried, not just its text: without it the log has no stack, and the
    /// source is what says WHICH of the module's parts failed — the two together are the whole line.</summary>
    [Fact]
    public void Error_CarriesBothTheSourceAndTheException()
    {
        var recorder = new Recorder();
        var failure = new InvalidOperationException("connection refused");

        new MqttLog(recorder).Error("MqttConnection.ApplyAsync", failure);

        var line = Assert.Single(recorder.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains("MqttConnection.ApplyAsync", line.Message, StringComparison.Ordinal);
        Assert.Same(failure, line.Exception);
    }

    /// <summary>The module reports some failures with no exception to hand over. That still has to be
    /// logged — a failure that says nothing is worse than one with no stack.</summary>
    [Fact]
    public void Error_WithNoExceptionStillWritesTheSource()
    {
        var recorder = new Recorder();

        new MqttLog(recorder).Error("MqttProbe", null);

        var line = Assert.Single(recorder.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains("MqttProbe", line.Message, StringComparison.Ordinal);
        Assert.Null(line.Exception);
    }
}
