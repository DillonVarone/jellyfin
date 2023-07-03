#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public class SharedBufferedHttpStream : LiveStream, IDirectStreamProvider
    {
        private readonly IServerApplicationHost _appHost;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IServerApplicationPaths _appPaths;
        private readonly IServerConfigurationManager _serverConfigurationManager;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClientFactory _httpClientFactory;
        private TaskCompletionSource<bool> _taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process _process;
        private Stream _logFileStream;
        private bool _hasExited;
        private bool _disposed = false;
        private bool _isHLS = false;

        private const int MaxHttpsToHttpRedirects = 5;

        public SharedBufferedHttpStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tunerHostInfo,
            string originalStreamId,
            string sessionId,
            IFileSystem fileSystem,
            ILogger logger,
            IConfigurationManager configurationManager,
            IServerApplicationHost appHost,
            IStreamHelper streamHelper,
            IMediaEncoder mediaEncoder,
            IServerApplicationPaths appPaths,
            IServerConfigurationManager serverConfigurationManager,
            IHttpClientFactory httpClientFactory)
            : base(mediaSource, tunerHostInfo, fileSystem, logger, configurationManager, streamHelper)
        {
            _httpClientFactory = httpClientFactory;
            _fileSystem = fileSystem;
            _appHost = appHost;
            _mediaEncoder = mediaEncoder;
            _appPaths = appPaths;
            _serverConfigurationManager = serverConfigurationManager;
            OriginalStreamId = originalStreamId;
            EnableStreamSharing = true;
            IsEndless = true;

            SessionIds.TryAdd(sessionId, "");
        }

        private static bool IsRedirect(HttpResponseMessage response)
        {
            int statusCode = (int)response.StatusCode;
            return statusCode >= 300 && statusCode < 400 && response.Headers.Location != null;
        }

        public override async Task Open(CancellationToken openCancellationToken)
        {
            LiveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();

            var mediaSource = OriginalMediaSource;

            string url = mediaSource.Path;
            string scratchUrl = url;
            string redirectUrl = null;
            int httpsToHttpRedirects = 0;

            Directory.CreateDirectory(Path.GetDirectoryName(TempFilePath) ?? throw new InvalidOperationException("Path can't be a root directory."));

            var typeName = GetType().Name;
            Logger.LogInformation("Opening {StreamType} Live stream from {Url}", typeName, url);

            var success = false;
            var backupIndex = 0;
            while (!string.IsNullOrEmpty(scratchUrl) && !success) {
                /* max latency to determine stream validity is 10s */
                var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                /* link this token with open cancellation */
                var openCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, openCancellationToken);
                try {
                    /* setup HTTP client */
                    var testClient = _httpClientFactory.CreateClient(NamedClient.Default);
                    mediaSource.RequiredHttpHeaders.ToList().ForEach(header => testClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value));

                    /* check if url can be reached */
                    using var testResponse = await testClient.GetAsync(scratchUrl, HttpCompletionOption.ResponseHeadersRead, openCts.Token)
                        .ConfigureAwait(false);

                    redirectUrl = null;
                    if (IsRedirect(testResponse)) {
                        Uri redirectUri = testResponse.Headers.Location;

                        /* If the redirect URI is relative, resolve it based on the initial URI */
                        if (!redirectUri.IsAbsoluteUri)
                        {
                            redirectUri = new Uri(new Uri(scratchUrl), redirectUri);
                        }

                        /* Check if the redirect URL is HTTP */
                        if (redirectUri.Scheme == Uri.UriSchemeHttp)
                        {
                            Logger.LogInformation("Redirecting from HTTPS to HTTP - {0}", scratchUrl);
                        }

                        redirectUrl = redirectUri.ToString();
                        httpsToHttpRedirects++;
                    } else if (testResponse.IsSuccessStatusCode) {
                        var contentType = testResponse.Content.Headers.ContentType?.ToString() ?? string.Empty;
                        if (contentType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase))
                        {
                            _isHLS = true;
                        }

                        /* check if there is even any valid data in the stream */
                        byte[] testBuffer = new byte[1024];
                        await using var testStream = await testResponse.Content.ReadAsStreamAsync(openCancellationToken).ConfigureAwait(false);
                        int testBytesRead = 0;
                        try
                        {
                            // ReadAsync with CancellationToken
                            testBytesRead = await testStream.ReadAsync(testBuffer, 0, testBuffer.Length, openCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            Logger.LogError("Timed out reading test bytes from stream {Url}", url);
                        }

                        if (testBytesRead > 0)
                        {
                            Logger.LogInformation("Successfully checked stream {0} contains data", url);
                            success = true;
                        }
                        else
                        {
                            Logger.LogError("Error checking stream {Url}, the stream was empty.", url);
                        }
                    } else {
                        Logger.LogError("Error connecting to stream {0}, with code {1}", url, testResponse.StatusCode);
                    }
                } catch (HttpRequestException ex) {
                    Logger.LogError(ex, "Error connecting to stream {Url}", url);
                } catch (TaskCanceledException) {
                    Logger.LogError("Timed out reading test bytes from stream {Url}", url);
                } finally {
                    timeoutCts.Dispose();
                    openCts.Dispose();
                }

                if (!success) {
                    /* try backup url */
                    if (!string.IsNullOrEmpty(redirectUrl) &&  httpsToHttpRedirects < MaxHttpsToHttpRedirects) {
                        /* follow redirect */
                        scratchUrl = redirectUrl;
                        httpsToHttpRedirects++;
                    } else if (mediaSource.BackupPaths.Count > backupIndex) {
                        url = mediaSource.BackupPaths[backupIndex];
                        scratchUrl = url;
                        backupIndex++;
                        httpsToHttpRedirects = 0;
                    } else {
                        url = "";
                        scratchUrl = url;
                        httpsToHttpRedirects = 0;
                    }
                }
            }

            if (!success) {
                throw new LiveTvConflictException("Failed to find a valid source for media");
            }

            // if (_isHLS)
            // {
            //     SetTempFilePath("m3u8");
            // }
            // else
            // {
            //     SetTempFilePath("ts");
            // }
            SetTempFilePath("ts");

            Logger.LogInformation("Beginning {StreamType} stream to {FilePath}", GetType().Name, TempFilePath);

            // start ffmpeg/proxy
            var arguments = GetFfmpegCommandLineArgs(mediaSource, url);
            var encoder = _mediaEncoder.EncoderPath;
            if (!string.IsNullOrEmpty(_mediaEncoder.MpegtsProxyPath) && !_isHLS) {
                SetCacheTempFilePath("cache");
                arguments = GetMpegtsProxyCommandLineArgs(mediaSource, url);
                encoder = _mediaEncoder.MpegtsProxyPath;
            }

            var processStartInfo = new ProcessStartInfo
            {
                CreateNoWindow = true,
                UseShellExecute = false,

                RedirectStandardError = true,
                RedirectStandardInput = true,

                FileName = encoder,
                Arguments = arguments,

                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };

            Logger.LogInformation("{Filename} {Arguments}", processStartInfo.FileName, processStartInfo.Arguments);

            var logFilePath = Path.Combine(_appPaths.LogDirectoryPath, "buffered-proxy" + Guid.NewGuid() + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            // Proxy writes debug/error info to stderr. This is useful when debugging so let's put it in the log directory.
            _logFileStream = new FileStream(logFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(_logFileStream, mediaSource, _jsonOptions, LiveStreamCancellationTokenSource.Token).ConfigureAwait(false);
            await _logFileStream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine + Environment.NewLine + processStartInfo.FileName + " " + processStartInfo.Arguments + Environment.NewLine + Environment.NewLine), LiveStreamCancellationTokenSource.Token).ConfigureAwait(false);

            _process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };
            _process.Exited += (_, _) => OnProcessExited(_process);

            _process.Start();

            LiveStreamCancellationTokenSource.Token.Register(Stop);

            DateOpened = DateTime.UtcNow;

            // Important - don't await the log task or we won't be able to kill proxy when the user stops playback
            _ = StartStreamingLog(_process.StandardError.BaseStream, _logFileStream);

            // wait 5s to start
            int timeout = 0;
            while (!File.Exists(TempFilePath) && !LiveStreamCancellationTokenSource.Token.IsCancellationRequested && timeout < 5000)
            {
                timeout += 50;
                await Task.Delay(50, LiveStreamCancellationTokenSource.Token).ConfigureAwait(false);
            }

            if (!File.Exists(TempFilePath))
            {
                // failed to start, do nothing for now
            }

            Logger.LogInformation("Stream proxy process started for {Path}", TempFilePath);

            MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream";
            if (_isHLS)
            {
                MediaSource.Path += ".m3u8";
            }
            else
            {
                MediaSource.Path += ".ts";
            }
            MediaSource.Protocol = MediaProtocol.Http;
        }

        private string GetFfmpegCommandLineArgs(MediaSourceInfo mediaSource, string url)
        {
            var inputFlags = new List<string>();

            // if (!_isHLS)
            // {
            //     inputFlags.Add("+discardcorrupt");

            //     inputFlags.Add("+igndts");

            //     inputFlags.Add("+genpts");
            // }
            inputFlags.Add("+discardcorrupt");

            inputFlags.Add("+igndts");

            inputFlags.Add("+genpts");

            var inputModifier = "";

            if (inputFlags.Count > 0)
            {
                inputModifier += " -fflags " + string.Join(string.Empty, inputFlags);
            }

            inputModifier += " -reconnect 1 -reconnect_on_network_error 1 -reconnect_on_http_error 1 -reconnect_streamed 1 -reconnect_delay_max 3";

            if (!_isHLS)
            {
                inputFlags.Add(" -reconnect_at_eof 1");
            }

            if (mediaSource.AnalyzeDurationMs > 0)
            {
                int analyzeDuration = (mediaSource.AnalyzeDurationMs ?? 0) * 1000;
                inputModifier += " -analyzeduration " + analyzeDuration.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                inputModifier += " -analyzeduration 1000000";
            }

            var hlsOptions = string.Empty;
            if (_isHLS)
            {
                inputModifier += " -f hls";
                // hlsOptions += " -f hls";
                // hlsOptions += " -hls_list_size 0";
                // hlsOptions += " -hls_segment_filename \"" +  Path.Combine(_serverConfigurationManager.GetTranscodePath(), UniqueId + "%d.ts") + "\"";
                // hlsOptions += " -hls_base_url \"hls/" + UniqueId + "/\"";
            }

            string outputArgs = "-c copy";

            var threads = EncodingHelper.GetNumberOfThreads(null, _serverConfigurationManager.GetEncodingOptions(), null);
            var commandLineArgs = string.Format(
                CultureInfo.InvariantCulture,
                "-loglevel 48 -i \"{0}\" {2} -threads {3}{4} -y \"{1}\"",
                url,
                TempFilePath.Replace("\"", "\\\"", StringComparison.Ordinal), // Escape quotes in filename
                outputArgs,
                threads,
                hlsOptions);

            return inputModifier + " " + commandLineArgs;
        }

        private string GetMpegtsProxyCommandLineArgs(MediaSourceInfo mediaSource, string url)
        {
            var commandLineArgs = string.Format(
                CultureInfo.InvariantCulture,
                "-h \"{0}\" {1} \"{2}\"",
                url,
                TempFilePath.Replace("\"", "\\\"", StringComparison.Ordinal),
                TempCacheFilePath.Replace("\"", "\\\"", StringComparison.Ordinal));

            return commandLineArgs;
        }

        private void Stop()
        {
            if (!_hasExited)
            {
                try
                {
                    Logger.LogInformation("Stopping buffering process for {Path}", TempFilePath);

                    _process.StandardInput.WriteLine("q");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error stopping buffering transcoding job for {Path}", TempFilePath);
                }

                if (_hasExited)
                {
                    return;
                }

                try
                {
                    Logger.LogInformation("Calling buffering process.WaitForExit for {Path}", TempFilePath);

                    if (_process.WaitForExit(10000))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error waiting for buffering process to exit for {Path}", TempFilePath);
                }

                if (_hasExited)
                {
                    return;
                }

                try
                {
                    Logger.LogInformation("Killing buffering process for {Path}", TempFilePath);

                    _process.Kill();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error killing buffering transcoding job for {Path}", TempFilePath);
                }
            }
        }

        private void OnProcessExited(Process process)
        {
            using (process)
            {
                _hasExited = true;

                _logFileStream?.Dispose();
                _logFileStream = null;

                var exitCode = process.ExitCode;

                Logger.LogInformation("Buffering exited with code {ExitCode} for {Path}", exitCode, TempFilePath);

                if (exitCode == 0)
                {
                    _taskCompletionSource.TrySetResult(true);
                }
                else
                {
                    _taskCompletionSource.TrySetException(
                        new FfmpegException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Buffering for {0} failed. Exit code {1}",
                                TempFilePath,
                                exitCode)));
                }
            }

            EnableStreamSharing = false;

            Logger.LogInformation("Buffering of {StreamType} to {FilePath} ended", GetType().Name, TempFilePath);
        }

        private async Task StartStreamingLog(Stream source, Stream target)
        {
            try
            {
                using (var reader = new StreamReader(source))
                {
                    await foreach (var line in reader.ReadAllLinesAsync().ConfigureAwait(false))
                    {
                        var bytes = Encoding.UTF8.GetBytes(Environment.NewLine + line);

                        await target.WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
                        await target.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error reading stream proxy recording log");
            }
        }

        private void deletePartialStreamFiles()
        {
            var directory = Path.GetDirectoryName(TempFilePath)
                            ?? throw new ArgumentException("Path can't be a root directory.", nameof(TempFilePath));

            var name = Path.GetFileNameWithoutExtension(TempFilePath);

            var filesToDelete = _fileSystem.GetFilePaths(directory).Where(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));

            foreach (var file in filesToDelete)
            {
                try
                {
                    Logger.LogDebug("Deleting HLS file {0}", file);
                    _fileSystem.DeleteFile(file);
                }
                catch (IOException ex)
                {
                    Logger.LogError(ex, "Error deleting HLS file {Path}", file);
                }
            }
        }

        private async Task deleteTempFiles()
        {
            if (!string.IsNullOrEmpty(TempCacheFilePath))
            {
                await DeleteTempFiles(TempCacheFilePath).ConfigureAwait(false);
            }
            await DeleteTempFiles(TempFilePath).ConfigureAwait(false);
        }

        public override async Task Close()
        {
            EnableStreamSharing = false;

            Logger.LogInformation("Closing {Type}", GetType().Name);

            await LiveStreamCancellationTokenSource.CancelAsync();

            // Block until ffmpeg/proxy exits
            try
            {
                await _taskCompletionSource.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Stream proxy exited with an error");
            }

            await deleteTempFiles().ConfigureAwait(false);
            // if (_isHLS)
            // {
            //     deletePartialStreamFiles();
            // }

            Dispose(true);
        }

        public override bool IsAlive()
        {
            return _taskCompletionSource.Task.IsCompleted;
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool dispose)
        {
            if (_disposed)
            {
                base.Dispose(dispose);
                return;
            }

            if (dispose)
            {
                _logFileStream?.Dispose();
                _process?.Dispose();
            }

            _logFileStream = null;
            _process = null;

            base.Dispose(dispose);
            _disposed = true;
        }
    }
}
