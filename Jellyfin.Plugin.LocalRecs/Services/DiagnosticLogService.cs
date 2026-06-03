using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Writes and reads the diagnostic log produced at the end of each recommendation run.
    /// </summary>
    public class DiagnosticLogService
    {
        private readonly ILogger<DiagnosticLogService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DiagnosticLogService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public DiagnosticLogService(ILogger<DiagnosticLogService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Appends content to the existing log file without overwriting it.</summary>
        /// <param name="content">The text to append.</param>
        public void Append(string content)
        {
            try
            {
                var path = GetLogFilePath();
                File.AppendAllText(path, content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to append to diagnostic log");
            }
        }

        /// <summary>Saves the log content to disk, overwriting any previous run.</summary>
        /// <param name="content">The text to write.</param>
        public void Save(string content)
        {
            try
            {
                var path = GetLogFilePath();
                File.WriteAllText(path, content);
                _logger.LogDebug("Saved diagnostic log ({Bytes} bytes) to {Path}", content.Length, path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save diagnostic log");
            }
        }

        /// <summary>Loads the log file.</summary>
        /// <returns>Log content and last-write timestamp, or null if no run has been recorded yet.</returns>
        public (string Content, DateTime LastModified)? Load()
        {
            try
            {
                var path = GetLogFilePath();
                if (!File.Exists(path))
                {
                    return null;
                }

                return (File.ReadAllText(path), File.GetLastWriteTimeUtc(path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load diagnostic log");
                return null;
            }
        }

        private string GetLogFilePath()
        {
            var dataPath = Plugin.Instance?.DataFolderPath
                ?? throw new InvalidOperationException("Plugin instance is not yet available");
            return Path.Combine(dataPath, "recommendation_log.txt");
        }
    }
}
