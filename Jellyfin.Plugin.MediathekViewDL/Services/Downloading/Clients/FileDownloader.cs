using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediathekViewDL.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Jellyfin.Plugin.MediathekViewDL.Services.Downloading.Clients;

/// <summary>
/// Service for downloading files from a URL.
/// </summary>
public partial class FileDownloader : IFileDownloader
{
    private readonly ILogger<FileDownloader> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigurationProvider _configurationProvider;

    private static readonly AsyncRetryPolicy<HttpResponseMessage> _resiliencePolicy = Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    /// <summary>
    /// Initializes a new instance of the <see cref="FileDownloader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="configurationProvider">The configuration provider.</param>
    public FileDownloader(ILogger<FileDownloader> logger, IHttpClientFactory httpClientFactory, IConfigurationProvider configurationProvider)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configurationProvider = configurationProvider;
    }

    /// <summary>
    /// Downloads a file from a URL to a specified destination path.
    /// </summary>
    /// <param name="fileUrl">The URL of the file to download.</param>
    /// <param name="destinationPath">The full path where the file should be saved.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the download was successful, otherwise false.</returns>
    public async Task<bool> DownloadFileAsync(string fileUrl, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pluginConfig = _configurationProvider.ConfigurationOrNull;

        // Validate file URL
        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            LogFileUrlEmpty();
            return false;
        }

        // Validate destination path
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            LogDestinationPathEmpty(fileUrl);
            return false;
        }

        // Validate plugin configuration
        if (pluginConfig is null)
        {
            LogPluginConfigUnavailable();
            return false;
        }

        // Check if the domain is allowed
        var domainAllowed = CheckDomainAllowed(fileUrl, pluginConfig, pluginConfig.Network.AllowUnknownDomains);
        if (!pluginConfig.Network.AllowUnknownDomains && !domainAllowed)
        {
            LogUnknownDomainNotAllowed(fileUrl);
            return false;
        }

        LogStartingDownload(fileUrl, destinationPath);

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var diskSpace = GetDiskSpace(destinationPath);

            if (diskSpace == null)
            {
                if (pluginConfig.Maintenance.AllowDownloadOnUnknownDiskSpace)
                {
                    LogDiskSpaceUnknownProceeding(destinationPath);
                }
                else
                {
                    LogDiskSpaceUnknownBlocked(destinationPath);
                    return false;
                }
            }
            else
            {
                // Check if there is enough disk space before starting the download
                if (diskSpace < pluginConfig.Download.MinFreeDiskSpaceBytes)
                {
                    LogInsufficientDiskSpace(fileUrl, destinationPath, pluginConfig.Download.MinFreeDiskSpaceBytes, diskSpace);
                    return false;
                }
            }

            var httpClient = _httpClientFactory.CreateClient("FileDownloaderClient");
            using var response = await _resiliencePolicy.ExecuteAsync(
                async ct => await httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            // Check disk space again considering the file size
            if (totalBytes != -1 && diskSpace != null)
            {
                var requiredSpace = totalBytes + pluginConfig.Download.MinFreeDiskSpaceBytes;

                if (diskSpace < requiredSpace)
                {
                    LogNotEnoughDiskSpace(fileUrl, destinationPath, requiredSpace, totalBytes, pluginConfig.Download.MinFreeDiskSpaceBytes, diskSpace);

                    return false;
                }
            }

            var receivedBytes = 0L;
            Memory<byte> buffer = new byte[8192];

#pragma warning disable CA2007 // Aufruf von "ConfigureAwait" für erwarteten Task erwägen
            await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
#pragma warning disable CA2007 // Aufruf von "ConfigureAwait" für erwarteten Task erwägen
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                if (pluginConfig.Download.MaxBandwidthMBits > 0)
                {
                    var bytesPerSecond = (long)pluginConfig.Download.MaxBandwidthMBits * 1_000_000 / 8;
                    stream = new ThrottledStream(stream, bytesPerSecond);
                }

                await using (stream)
                {
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await fileStream.WriteAsync(buffer.Slice(0, read), cancellationToken).ConfigureAwait(false);
                        receivedBytes += read;
                        if (totalBytes != -1)
                        {
                            progress?.Report((double)receivedBytes / totalBytes * 100);
                        }
                    }
                }
#pragma warning restore CA2007 // Aufruf von "ConfigureAwait" für erwarteten Task erwägen
            }
#pragma warning restore CA2007 // Aufruf von "ConfigureAwait" für erwarteten Task erwägen

            LogDownloadSucceeded(destinationPath);

            return true;
        }
        catch (HttpRequestException ex)
        {
            LogHttpRequestFailed(ex, fileUrl, ex.Message);
            return false;
        }
        catch (IOException ex)
        {
            LogFileSystemErrorDownload(ex, fileUrl, destinationPath, ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            LogDownloadCancelled(fileUrl, destinationPath);
            // Clean up partially downloaded file
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            return false;
        }
        catch (Exception ex)
        {
            LogUnexpectedDownloadError(ex, fileUrl, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Generates a streaming URL file (.strm) at the specified destination path.
    /// </summary>
    /// <param name="fileUrl">The URL to be written into the streaming URL file.</param>
    /// <param name="destinationPath">The file path where the streaming URL file will be created.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>True if the streaming URL file was successfully created, otherwise false.</returns>
    public async Task<bool> GenerateStreamingUrlFileAsync(string fileUrl, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(destinationPath, fileUrl, cancellationToken).ConfigureAwait(false);

            LogStreamingUrlFileCreated(destinationPath);

            return true;
        }
        catch (IOException ex)
        {
            LogFileSystemErrorStreamingUrl(ex, destinationPath, ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            LogStreamingUrlCreationCancelled(destinationPath);
            // Clean up partially created file
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            return false;
        }
        catch (Exception ex)
        {
            LogUnexpectedStreamingUrlError(ex, destinationPath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Gets the available free disk space in bytes for the drive containing the specified path.
    /// </summary>
    /// <param name="path">The path to check disk space for.</param>
    /// <returns>The available free disk space in bytes, or null if it could not be determined.</returns>
    public static long? GetDiskSpace(string path)
    {
        var directory = path;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return 0;
        }

#pragma warning disable CA3003 // The path is provieded by the Admin. Also there should be no issue with directory traversal. As we only check disk space, this is acceptable.
        if (!Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(path)!;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return 0;
            }
        }
#pragma warning restore CA3003

        directory = Path.GetFullPath(directory);

        try
        {
            var drive = new DriveInfo(directory);
            return drive.AvailableFreeSpace;
        }
        catch (Exception)
        {
            // This can happen for UNC paths, etc.
            return null;
        }
    }

    private bool CheckDomainAllowed(string fileUrl, PluginConfiguration pluginConfig, bool isWarningOnly = false)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var uriResult))
        {
            var logLevel = isWarningOnly ? LogLevel.Warning : LogLevel.Error;
            LogInvalidUrl(logLevel, fileUrl);

            return false;
        }

        var host = uriResult.Host;

        // Extract the top-level domain for validation
        var hostParts = host.Split('.');
        if (hostParts.Length < 2)
        {
            var logLevel = isWarningOnly ? LogLevel.Warning : LogLevel.Error;
            LogInvalidHost(logLevel, host);

            return false;
        }

        var topDomain = string.Join('.', hostParts[^2..]);

        if (!pluginConfig.AllowedDomains.Contains(topDomain))
        {
            var logLevel = isWarningOnly ? LogLevel.Warning : LogLevel.Error;
            LogDomainNotAllowed(logLevel, topDomain);

            return false;
        }

        return true;
    }

    #region Logging

    [LoggerMessage(Level = LogLevel.Error, Message = "File URL cannot be null or empty.")]
    private partial void LogFileUrlEmpty();

    [LoggerMessage(Level = LogLevel.Error, Message = "Destination path cannot be null or empty for URL: {FileUrl}")]
    private partial void LogDestinationPathEmpty(string? fileUrl);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plugin configuration is not available.")]
    private partial void LogPluginConfigUnavailable();

    [LoggerMessage(Level = LogLevel.Error, Message = "Download from unknown domain is not allowed: {FileUrl}")]
    private partial void LogUnknownDomainNotAllowed(string? fileUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting download of '{FileUrl}' to '{DestinationPath}'")]
    private partial void LogStartingDownload(string? fileUrl, string? destinationPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not determine available disk space for '{DestinationPath}'. Proceeding with download as configured.")]
    private partial void LogDiskSpaceUnknownProceeding(string? destinationPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not determine available disk space for '{DestinationPath}'. Download blocked. Enable 'Allow download on unknown disk space' in settings to bypass this check.")]
    private partial void LogDiskSpaceUnknownBlocked(string? destinationPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Insufficient disk space to download '{FileUrl}' to '{DestinationPath}'. Required: {RequiredBytes} bytes, Available: {AvailableBytes} bytes.")]
    private partial void LogInsufficientDiskSpace(string? fileUrl, string? destinationPath, long requiredBytes, long? availableBytes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Not enough disk space to download '{FileUrl}' to '{DestinationPath}'. Required: {RequiredBytes} bytes (File: {FileSize} + MinFree: {MinFree}), Available: {AvailableBytes}.")]
    private partial void LogNotEnoughDiskSpace(string? fileUrl, string? destinationPath, long requiredBytes, long fileSize, long minFree, long? availableBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully downloaded '{DestinationPath}'.")]
    private partial void LogDownloadSucceeded(string? destinationPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "HTTP request failed during download of '{FileUrl}': {Message}")]
    private partial void LogHttpRequestFailed(Exception ex, string? fileUrl, string? message);

    [LoggerMessage(Level = LogLevel.Error, Message = "File system error during download of '{FileUrl}' to '{DestinationPath}': {Message}")]
    private partial void LogFileSystemErrorDownload(Exception ex, string? fileUrl, string? destinationPath, string? message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download of '{FileUrl}' to '{DestinationPath}' was cancelled.")]
    private partial void LogDownloadCancelled(string? fileUrl, string? destinationPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unexpected error occurred during download of '{FileUrl}': {Message}")]
    private partial void LogUnexpectedDownloadError(Exception ex, string? fileUrl, string? message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully created streaming URL file at '{DestinationPath}'.")]
    private partial void LogStreamingUrlFileCreated(string? destinationPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "File system error during creation of streaming URL file at '{DestinationPath}': {Message}")]
    private partial void LogFileSystemErrorStreamingUrl(Exception ex, string? destinationPath, string? message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creation of streaming URL file at '{DestinationPath}' was cancelled.")]
    private partial void LogStreamingUrlCreationCancelled(string? destinationPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unexpected error occurred during creation of streaming URL file at '{DestinationPath}': {Message}")]
    private partial void LogUnexpectedStreamingUrlError(Exception ex, string? destinationPath, string? message);

    [LoggerMessage(Message = "Invalid URL: {FileUrl}")]
    private partial void LogInvalidUrl(LogLevel level, string? fileUrl);

    [LoggerMessage(Message = "Invalid host in URL: {Host}")]
    private partial void LogInvalidHost(LogLevel level, string? host);

    [LoggerMessage(Message = "Domain '{Domain}' is not allowed.")]
    private partial void LogDomainNotAllowed(LogLevel level, string? domain);

    #endregion
}
