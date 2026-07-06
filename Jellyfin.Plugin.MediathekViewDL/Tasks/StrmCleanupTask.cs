using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediathekViewDL.Configuration;
using Jellyfin.Plugin.MediathekViewDL.Services;
using Jellyfin.Plugin.MediathekViewDL.Services.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediathekViewDL.Tasks;

/// <summary>
/// Scheduled task to clean up invalid .strm files.
/// </summary>
public partial class StrmCleanupTask : IScheduledTask
{
    private const long MaxStrmFileSize = 4096; // 4 KB max size for .strm files to prevent accidents
    private readonly ILogger<StrmCleanupTask> _logger;
    private readonly IStrmValidationService _validationService;
    private readonly IConfigurationProvider _configurationProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmCleanupTask"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="validationService">The validation service.</param>
    /// <param name="configurationProvider">The configuration provider.</param>
    public StrmCleanupTask(ILogger<StrmCleanupTask> logger, IStrmValidationService validationService, IConfigurationProvider configurationProvider)
    {
        _logger = logger;
        _validationService = validationService;
        _configurationProvider = configurationProvider;
    }

    /// <inheritdoc />
    public string Name => "Mediathek .strm Bereinigung";

    /// <inheritdoc />
    public string Key => "MediathekStrmCleanup";

    /// <inheritdoc />
    public string Category => "Mediathek Downloader";

    /// <inheritdoc />
    public string Description => "Überprüft .strm Dateien auf Gültigkeit und löscht verwaiste Links.";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.InitializationException is not null)
        {
            LogCleanupAborted(Plugin.Instance.InitializationException.Message);
            return;
        }

        var config = _configurationProvider.ConfigurationOrNull;
        if (config == null || !config.Maintenance.EnableStrmCleanup)
        {
            LogCleanupDisabled();
            return;
        }

        LogCleanupStarting();
        progress.Report(0);

        var subscriptions = config.Subscriptions.Where(s => s.IsEnabled).ToList();
        if (subscriptions.Count == 0)
        {
            LogNoSubscriptions();
            return;
        }

        // Collect all distinct download paths
        var paths = new HashSet<string>();

        // Add default download path
        if (!string.IsNullOrWhiteSpace(config.Paths.DefaultDownloadPath))
        {
            paths.Add(config.Paths.DefaultDownloadPath);
        }

        // Add subscription specific paths
        foreach (var sub in subscriptions)
        {
            if (!string.IsNullOrWhiteSpace(sub.Download.DownloadPath))
            {
                paths.Add(sub.Download.DownloadPath);
            }
        }

        var pathList = paths.ToList();
        var totalPaths = pathList.Count;
        var filesProcessed = 0;
        var filesDeleted = 0;

        for (int i = 0; i < totalPaths; i++)
        {
            var path = pathList[i];
            if (!Directory.Exists(path))
            {
                LogDirectoryNotFound(path);
                continue;
            }

            try
            {
                var strmFiles = Directory.GetFiles(path, "*.strm", SearchOption.AllDirectories);
                var totalFiles = strmFiles.Length;

                LogFoundFiles(totalFiles, path);

                for (int j = 0; j < totalFiles; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var filePath = strmFiles[j];
                    var fileInfo = new FileInfo(filePath);

                    // Safety check: File size
                    if (fileInfo.Length > MaxStrmFileSize)
                    {
                        LogSkippingLargeFile(filePath, MaxStrmFileSize, fileInfo.Length);
                        continue;
                    }

                    try
                    {
                        var url = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                        url = url.Trim();

                        var isValid = await _validationService.ValidateUrlAsync(url, cancellationToken).ConfigureAwait(false);

                        if (!isValid)
                        {
                            LogDeletingInvalidFile(filePath, url);

                            File.Delete(filePath);
                            filesDeleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFileProcessingError(ex, filePath);
                    }

                    filesProcessed++;

                    // Calculate progress based on paths processed + files processed within current path
                    // This is a rough estimation
                    double pathProgress = (double)i / totalPaths * 100;
                    double fileProgress = (double)(j + 1) / totalFiles * (100.0 / totalPaths);
                    progress.Report(pathProgress + fileProgress);
                }
            }
            catch (Exception ex)
            {
                LogDirectoryScanError(ex, path);
            }
        }

        LogCleanupFinished(filesProcessed, filesDeleted);

        progress.Report(100);
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = ".strm cleanup task aborted because the plugin failed to initialize: {ErrorMessage}")]
    private partial void LogCleanupAborted(string? errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Strm cleanup task is disabled in configuration or config is missing. Skipping.")]
    private partial void LogCleanupDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting .strm cleanup task.")]
    private partial void LogCleanupStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "No active subscriptions found. Task finished.")]
    private partial void LogNoSubscriptions();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Directory not found: {Path}")]
    private partial void LogDirectoryNotFound(string? path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found {Count} .strm files in '{Path}'.")]
    private partial void LogFoundFiles(int count, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping file '{Path}' because it is larger than {Max} bytes ({Size}).")]
    private partial void LogSkippingLargeFile(string? path, long max, long size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleting invalid .strm file: '{Path}' (URL: {Url})")]
    private partial void LogDeletingInvalidFile(string? path, string? url);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing file '{Path}'.")]
    private partial void LogFileProcessingError(Exception ex, string? path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error scanning directory '{Path}'.")]
    private partial void LogDirectoryScanError(Exception ex, string? path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Strm cleanup task finished. Processed {Processed} files, deleted {Deleted} files.")]
    private partial void LogCleanupFinished(int processed, int deleted);

    #endregion
}
