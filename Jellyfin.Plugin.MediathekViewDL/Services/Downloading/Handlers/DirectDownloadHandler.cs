using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediathekViewDL.Configuration;
using Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Clients;
using Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Helpers;
using Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Handlers;

/// <summary>
/// Handler for direct file downloads.
/// </summary>
public partial class DirectDownloadHandler : BaseDownloadHandler
{
    private readonly ILogger<DirectDownloadHandler> _logger;
    private readonly IFileDownloader _fileDownloader;
    private readonly IConfigurationProvider _configProvider;
    private readonly IServerApplicationPaths _appPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectDownloadHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="fileDownloader">The file downloader service.</param>
    /// <param name="configProvider">The configuration provider.</param>
    /// <param name="appPaths">The application paths.</param>
    public DirectDownloadHandler(ILogger<DirectDownloadHandler> logger, IFileDownloader fileDownloader, IConfigurationProvider configProvider, IServerApplicationPaths appPaths)
    {
        _logger = logger;
        _fileDownloader = fileDownloader;
        _configProvider = configProvider;
        _appPaths = appPaths;
    }

    /// <inheritdoc />
    protected override bool IsEnabled => false;

    /// <inheritdoc />
    protected override DownloadType SupportedDownloadType { get => DownloadType.DirectDownload; }

    /// <inheritdoc />
    public override async Task<bool> ExecuteAsync(DownloadItem item, DownloadJob job, IProgress<double> progress, CancellationToken cancellationToken)
    {
        LogDownloading(job.Title, item.DestinationPath);

        var tempPath = TempFileHelper.GetTempFilePath(item.DestinationPath, ".mkv", _configProvider, _appPaths, _logger);
        try
        {
            var res = await _fileDownloader.DownloadFileAsync(item.SourceUrl, tempPath, progress, cancellationToken).ConfigureAwait(false);
            if (!res)
            {
                return false;
            }

            File.Move(tempPath, item.DestinationPath);
            return true;
        }
        catch (Exception ex)
        {
            LogExtractOrMoveFailed(ex, item.DestinationPath);
            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    LogTempFileDeleteFailed(ex, tempPath);
                }
            }
        }
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading '{Title}' to '{Path}'.")]
    private partial void LogDownloading(string? title, string? path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to extract or move audio file for {DestinationPath}")]
    private partial void LogExtractOrMoveFailed(Exception ex, string? destinationPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete temporary audio file {TempPath}")]
    private partial void LogTempFileDeleteFailed(Exception ex, string? tempPath);

    #endregion
}
