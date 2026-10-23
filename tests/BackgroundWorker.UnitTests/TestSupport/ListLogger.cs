using Microsoft.Extensions.Logging;

namespace BackgroundWorker.UnitTests.TestSupport;

/// <summary>Collects what was logged, rendered the way a log sink would see it.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
