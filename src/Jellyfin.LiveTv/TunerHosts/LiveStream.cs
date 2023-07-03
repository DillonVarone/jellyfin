#nullable disable

#pragma warning disable CA1711
#pragma warning disable CS1591

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public class LiveStream : ILiveStream
    {
        private readonly IConfigurationManager _configurationManager;

        public LiveStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tuner,
            IFileSystem fileSystem,
            ILogger logger,
            IConfigurationManager configurationManager,
            IStreamHelper streamHelper)
        {
            OriginalMediaSource = mediaSource;
            FileSystem = fileSystem;
            MediaSource = mediaSource;
            Logger = logger;
            EnableStreamSharing = true;
            UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            if (tuner is not null)
            {
                TunerHostId = tuner.Id;
            }

            _configurationManager = configurationManager;
            StreamHelper = streamHelper;

            ConsumerCount = 1;
            SetTempFilePath("ts");

            SessionIds = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        protected IFileSystem FileSystem { get; }

        protected IStreamHelper StreamHelper { get; }

        protected ILogger Logger { get; }

        protected CancellationTokenSource LiveStreamCancellationTokenSource { get; } = new CancellationTokenSource();

        protected string TempFilePath { get; set; }

        protected string TempCacheFilePath { get; set; }

        public MediaSourceInfo OriginalMediaSource { get; set; }

        public MediaSourceInfo MediaSource { get; set; }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public bool EnableStreamSharing { get; set; }

        public string UniqueId { get; }

        public string TunerHostId { get; }

        public DateTime DateOpened { get; protected set; }

        public bool AllowCleanup { get; set; }

        public bool IsEndless { get; set; }
        public ConcurrentDictionary<string, string> SessionIds { get; set; }

        protected void SetTempFilePath(string extension)
        {
            TempFilePath = Path.Combine(_configurationManager.GetTranscodePath(), UniqueId + "." + extension);
        }

        protected void SetCacheTempFilePath(string extension)
        {
            TempCacheFilePath = Path.Combine(_configurationManager.GetTranscodePath(), UniqueId + "." + extension);
        }

        public virtual Task Open(CancellationToken openCancellationToken)
        {
            DateOpened = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public virtual async Task Close()
        {
            EnableStreamSharing = false;

            Logger.LogInformation("Closing {Type}", GetType().Name);

            await LiveStreamCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        public Stream GetStream()
        {
            var stream = new FileStream(
                TempFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                IODefaults.FileStreamBufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            double elapsedTime = (DateTime.UtcNow - DateOpened).TotalSeconds;

            if (elapsedTime > 10.0)
            {
                double streamDataRateBps = (double)stream.Length / elapsedTime;
                long offset = (long)(streamDataRateBps * 10.0);

                if (offset < stream.Length) {
                    Logger.LogInformation("Providing stream offset {0}s, {1}B from tip", 10.0, offset);
                    TrySeek(stream, -offset);
                }
            }

            return stream;
        }

        public virtual Task RegisterOwner(string sessionId)
        {
            SessionIds.AddOrUpdate(sessionId, "", (key, oldValue) => "");

            return Task.CompletedTask;
        }

        public virtual Task UnregisterOwner(string sessionId)
        {
            SessionIds.TryRemove(sessionId, out _);

            return Task.CompletedTask;
        }

        public virtual Task RegisterTranscoder(string sessionId, string transcodeJobId)
        {
            if (SessionIds.TryGetValue(sessionId, out var oldTranscodeJobId)) {
                SessionIds.TryUpdate(sessionId, transcodeJobId, oldTranscodeJobId);
            }

            return Task.CompletedTask;
        }

        public virtual bool IsAlive()
        {
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                LiveStreamCancellationTokenSource?.Dispose();
            }
        }

        protected async Task DeleteTempFiles(string path, int retryCount = 0)
        {
            if (retryCount == 0)
            {
                Logger.LogInformation("Deleting temp file {FilePath}", path);
            }

            try
            {
                FileSystem.DeleteFile(path);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting file {FilePath}", path);
                if (retryCount <= 40)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    await DeleteTempFiles(path, retryCount + 1).ConfigureAwait(false);
                }
            }
        }

        private void TrySeek(FileStream stream, long offset)
        {
            if (!stream.CanSeek)
            {
                return;
            }

            try
            {
                stream.Seek(offset, SeekOrigin.End);
            }
            catch (IOException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error seeking stream");
            }
        }
    }
}
