using System;
using System.IO;
using System.Threading;

namespace IBCollective2Sync
{
    public class FileLogger : IDisposable
    {
        private readonly string _logDirectory;
        private readonly string _applicationName;
        private readonly object _logLock = new object();
        private readonly Timer _flushTimer;
        private StreamWriter _logWriter;
        private string _currentLogFile;
        private bool _disposed = false;

        public string LogFilePath => _currentLogFile;

        public FileLogger(string applicationName)
        {
            _applicationName = applicationName;
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
            Directory.CreateDirectory(_logDirectory);

            UpdateLogFilePath();
            CleanupOldLogs();

            _flushTimer = new Timer(_ => FlushBuffer(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            WriteLog("INFO", "=== Logger initialized ===");
        }

        private void UpdateLogFilePath()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var newLogFile = Path.Combine(_logDirectory, $"{_applicationName}_{today}.log");

            if (_currentLogFile != newLogFile)
            {
                lock (_logLock)
                {
                    try
                    {
                        _logWriter?.Flush();
                        _logWriter?.Dispose();
                        _currentLogFile = newLogFile;
                        _logWriter = new StreamWriter(new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                            AutoFlush = true
                        };
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"LOGGER ERROR: Could not open log file {newLogFile}: {ex.Message}");
                        // Fallback to a temp file or just ignore - validation can rely on Console.
                         _currentLogFile = Path.Combine(Path.GetTempPath(), $"{_applicationName}_{Guid.NewGuid()}.log");
                         try {
                            _logWriter = new StreamWriter(new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                         } catch { /* Give up on file logging */ }
                    }
                }
            }
        }

        private void CleanupOldLogs()
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-30);
                var files = Directory.GetFiles(_logDirectory, "*.log");

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.CreationTime < cutoffDate)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Ignore individual file deletion errors
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private void WriteLog(string level, string message, Exception exception = null)
        {
            if (_disposed) return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level.PadRight(5)}] [{Thread.CurrentThread.ManagedThreadId:D3}] {message}";

                if (exception != null)
                {
                    logEntry += $"\nException: {exception}";
                }

                lock (_logLock)
                {
                    // Check if day changed
                    if (DateTime.Now.Date != new FileInfo(_currentLogFile).CreationTime.Date)
                    {
                         UpdateLogFilePath();
                    }

                    _logWriter.WriteLine(logEntry);
                }
            }
            catch
            {
                // Fail silently
            }
        }

        private void FlushBuffer()
        {
            if (_disposed) return;

            lock (_logLock)
            {
                try
                {
                     // Writer is AutoFlush=true, but we can force it or handle rotation checks here if needed.
                     // For now, just ensuring file path is correct is handled in WriteLog or here.
                     // Let's rely on WriteLog for rotation check to be more real-time,
                     // or do it here to avoid checking every write.

                     var today = DateTime.Now.ToString("yyyy-MM-dd");
                     if (!_currentLogFile.Contains(today))
                     {
                         UpdateLogFilePath();
                     }
                }
                catch
                {
                    // Ignore
                }
            }
        }

        public void Debug(string message) => WriteLog("DEBUG", message);
        public void Info(string message) => WriteLog("INFO", message);
        public void Warn(string message) => WriteLog("WARN", message);
        public void Error(string message, Exception exception = null) => WriteLog("ERROR", message, exception);

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _flushTimer?.Dispose();

            lock (_logLock)
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
        }
    }
}
