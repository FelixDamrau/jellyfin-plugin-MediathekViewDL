using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Handlers;
using Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Models;
using Jellyfin.Plugin.MediathekViewDL.Services.Library;
using Jellyfin.Plugin.MediathekViewDL.Services.Metadata;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Downloading;

/// <summary>
/// Service responsible for executing download jobs.
/// </summary>
public partial class DownloadManager : IDownloadManager
{
    private readonly ILogger<DownloadManager> _logger;
    private readonly INfoService _nfoService;
    private readonly IEnumerable<IDownloadHandler> _downloadHandlers;
    private readonly IStrmValidationService _urlValidationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="nfoService">The NFO service.</param>
    /// <param name="downloadHandlers">The download handlers.</param>
    /// <param name="urlValidationService">The URL validation service.</param>
    public DownloadManager(
        ILogger<DownloadManager> logger,
        INfoService nfoService,
        IEnumerable<IDownloadHandler> downloadHandlers,
        IStrmValidationService urlValidationService)
    {
        _logger = logger;
        _nfoService = nfoService;
        _downloadHandlers = downloadHandlers;
        _urlValidationService = urlValidationService;
    }

    /// <summary>
    /// Executes a single download job.
    /// </summary>
    /// <param name="job">The job to execute.</param>
    /// <param name="progress">The progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the job was successful (or file already existed), otherwise false.</returns>
    public async Task<bool> ExecuteJobAsync(DownloadJob job, IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogStartingDownloadJob(job.Title);

        var success = true;

        foreach (var item in job.DownloadItems)
        {
            LogProcessingDownloadItem(item.JobType, item.DestinationPath);

            if (File.Exists(item.DestinationPath))
            {
                LogFileAlreadyExists(item.DestinationPath);

                // Still continue execution so NFO and other files continue downloading.
                continue;
            }

            try
            {
                bool isValidUrl = await _urlValidationService.ValidateUrlAsync(item.SourceUrl, cancellationToken).ConfigureAwait(false);
                if (!isValidUrl)
                {
                    LogInvalidUrl(item.DestinationPath);
                    success = false;
                }
            }
            catch (Exception ex)
            {
                LogUrlValidationFailed(ex, item.SourceUrl);
                success = false;
            }

            var directory = Path.GetDirectoryName(item.DestinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    LogDirectoryCreationFailed(ex, directory);
                    success = false;
                    continue; // Skip this item
                }
            }

            var handler = _downloadHandlers.FirstOrDefault(h => h.CanHandle(item.JobType));
            if (handler != null)
            {
                success &= await handler.ExecuteAsync(item, job, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                LogNoHandlerFound(item.JobType);
                success = false;
            }
        }

        if (success)
        {
            progress.Report(100);

            if (job.NfoMetadata is not null && !File.Exists(job.NfoMetadata.FilePath))
            {
                _nfoService.CreateNfo(job.NfoMetadata);
            }
        }

        return success;
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting download job for '{Title}'.")]
    private partial void LogStartingDownloadJob(string? title);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing download item: {Type} -> {Path}")]
    private partial void LogProcessingDownloadItem(DownloadType type, string? path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "File '{Path}' already exists. Skipping download.")]
    private partial void LogFileAlreadyExists(string? path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid URL: {Url}")]
    private partial void LogInvalidUrl(string? url);

    [LoggerMessage(Level = LogLevel.Error, Message = "URL validation failed for {Url}")]
    private partial void LogUrlValidationFailed(Exception ex, string? url);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to create directory '{Directory}'.")]
    private partial void LogDirectoryCreationFailed(Exception ex, string? directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "No handler found for download type: {Type}")]
    private partial void LogNoHandlerFound(DownloadType type);

    #endregion
}
