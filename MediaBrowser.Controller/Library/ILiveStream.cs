#nullable disable

#pragma warning disable CA1711, CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Library
{
    public interface ILiveStream : IDisposable
    {
        int ConsumerCount { get; set; }

        string OriginalStreamId { get; set; }

        string TunerHostId { get; }

        bool EnableStreamSharing { get; }

        MediaSourceInfo MediaSource { get; set; }

        string UniqueId { get; }

        bool AllowCleanup { get; set; }

        ConcurrentDictionary<string, string> SessionIds { get; set; }

        Task Open(CancellationToken openCancellationToken);

        Task RegisterOwner(string sessionId);

        Task UnregisterOwner(string sessionId);

        Task RegisterTranscoder(string sessionId, string transcodeJobId);

        Task Close();

        Stream GetStream();

        bool IsAlive();
    }
}
