#nullable disable

#pragma warning disable CA1711, CS1591

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Library
{
    public interface ILiveStream
    {
        int ConsumerCount { get; set; }

        string OriginalStreamId { get; set; }

        string TunerHostId { get; }

        bool EnableStreamSharing { get; }

        MediaSourceInfo MediaSource { get; set; }

        string UniqueId { get; }

        bool AllowCleanup { get; set; }

        List<string> SessionIds { get; set; }

        Task Open(CancellationToken openCancellationToken);

        Task Close();

        Stream GetStream();
    }
}
