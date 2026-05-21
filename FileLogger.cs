using Microsoft.Extensions.Logging;

namespace HyperVNetworkSwitcher;

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddSimpleFileLogger(this ILoggingBuilder builder, string path)
    {
        builder.AddProvider(new FileLoggerProvider(path));
        return builder;
    }
}

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly StreamWriter _writer = new(path, append: true) { AutoFlush = true };
    private readonly Lock _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger(string category, StreamWriter writer, Lock lockObj) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level,-11}] {category}: {formatter(state, exception)}";
        if (exception is not null) line += $"\n{exception}";
        lock (lockObj) writer.WriteLine(line);
    }
}
