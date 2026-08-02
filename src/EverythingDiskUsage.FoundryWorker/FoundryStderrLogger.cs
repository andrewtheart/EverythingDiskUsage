using Microsoft.Extensions.Logging;

namespace EverythingDiskUsage.FoundryWorker;

internal sealed class FoundryStderrLogger : ILogger
{
    public static FoundryStderrLogger Instance { get; } = new();

    private FoundryStderrLogger()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        Console.Error.WriteLine($"[{logLevel}] {message}{(exception is null ? string.Empty : $" {exception.Message}")}");
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
