// TEMPORARY diagnostics — remove before committing.
using Microsoft.Extensions.Logging;

namespace Sample.MacOS;

sealed class DiagLoggerProvider : ILoggerProvider
{
    static readonly object Gate = new();
    static readonly string File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/appbridge-diag.log");
    public ILogger CreateLogger(string categoryName) => new DiagLogger(categoryName);
    public void Dispose() { }
    sealed class DiagLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Gate)
                System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : " :: " + exception)}\n");
        }
    }
}
