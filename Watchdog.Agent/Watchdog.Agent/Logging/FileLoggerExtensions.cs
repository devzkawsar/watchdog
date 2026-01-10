using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Logging;

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(
        this ILoggingBuilder builder,
        string path,
        LogLevel minimumLevel = LogLevel.Information,
        long? fileSizeLimitBytes = null,
        int? retainedFileCountLimit = null)
    {
        builder.AddProvider(new SimpleFileLoggerProvider(path, minimumLevel));
        return builder;
    }

    private sealed class SimpleFileLoggerProvider : ILoggerProvider
    {
        private readonly string _path;
        private readonly LogLevel _minimumLevel;
        private readonly ConcurrentDictionary<string, SimpleFileLogger> _loggers = new();

        public SimpleFileLoggerProvider(string path, LogLevel minimumLevel)
        {
            _path = path;
            _minimumLevel = minimumLevel;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, c => new SimpleFileLogger(_path, c, _minimumLevel));
        }

        public void Dispose()
        {
            _loggers.Clear();
        }
    }

    private sealed class SimpleFileLogger : ILogger
    {
        private static readonly object GlobalLock = new();

        private readonly string _path;
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public SimpleFileLogger(string path, string category, LogLevel minimumLevel)
        {
            _path = path;
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null)
                return;

            var line = $"{DateTimeOffset.UtcNow:o} [{logLevel}] {_category}: {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                lock (GlobalLock)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Swallow logging errors
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
