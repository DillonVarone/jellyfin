#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;
using PlaylistsNET.Content;

namespace MediaBrowser.Providers.Playlists
{
    public class PlaylistItemsProvider : ICustomMetadataProvider<Playlist>,
        IHasOrder,
        IForcedProvider,
        IPreRefreshProvider,
        IHasItemChangeMonitor
    {
        private readonly ILogger<PlaylistItemsProvider> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly CollectionType[] _ignoredCollections = [CollectionType.livetv, CollectionType.boxsets, CollectionType.playlists];

        public PlaylistItemsProvider(ILogger<PlaylistItemsProvider> logger, ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        public string Name => "Playlist Reader";

        // Run last
        public int Order => 100;

        public Task<ItemUpdateType> FetchAsync(Playlist item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var path = item.Path;
            if (!Playlist.IsPlaylistFile(path))
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            var extension = Path.GetExtension(path);
            if (!Playlist.SupportedExtensions.Contains(extension ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            var items = GetItems(path, extension).ToArray();

            item.LinkedChildren = items;

            return Task.FromResult(ItemUpdateType.None);
        }

        private IEnumerable<LinkedChild> GetItems(string path, string extension)
        {
            var libraryRoots = _libraryManager.GetUserRootFolder().Children
                                .OfType<CollectionFolder>()
                                .Where(f => f.CollectionType.HasValue && !_ignoredCollections.Contains(f.CollectionType.Value))
                                .SelectMany(f => f.PhysicalLocations)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

            using (var stream = File.OpenRead(path))
            {
                if (string.Equals(".wpl", extension, StringComparison.OrdinalIgnoreCase))
                {
                    return GetWplItems(stream, path, libraryRoots);
                }

                if (string.Equals(".zpl", extension, StringComparison.OrdinalIgnoreCase))
                {
                    return GetZplItems(stream, path, libraryRoots);
                }

                if (string.Equals(".m3u", extension, StringComparison.OrdinalIgnoreCase))
                {
                    return GetM3uItems(stream, path, libraryRoots);
                }

                if (string.Equals(".m3u8", extension, StringComparison.OrdinalIgnoreCase))
                {
                    return GetM3uItems(stream, path, libraryRoots);
                }

                if (string.Equals(".pls", extension, StringComparison.OrdinalIgnoreCase))
                {
                    return GetPlsItems(stream, path, libraryRoots);
                }
            }

            return Enumerable.Empty<LinkedChild>();
        }

        private IEnumerable<LinkedChild> GetPlsItems(Stream stream, string playlistPath, List<string> libraryRoots)
        {
            var content = new PlsContent();
            var playlist = content.GetFromStream(stream);

            return playlist.PlaylistEntries
                    .Select(i => GetLinkedChild(i.Path, playlistPath, libraryRoots))
                    .Where(i => i is not null);
        }

        private IEnumerable<LinkedChild> GetM3uItems(Stream stream, string playlistPath, List<string> libraryRoots)
        {
            var content = new M3uContent();
            var playlist = content.GetFromStream(stream);

            return playlist.PlaylistEntries
                    .Select(i => GetLinkedChild(i.Path, playlistPath, libraryRoots))
                    .Where(i => i is not null);
        }

        private IEnumerable<LinkedChild> GetZplItems(Stream stream, string playlistPath, List<string> libraryRoots)
        {
            var content = new ZplContent();
            var playlist = content.GetFromStream(stream);

            return playlist.PlaylistEntries
                    .Select(i => GetLinkedChild(i.Path, playlistPath, libraryRoots))
                    .Where(i => i is not null);
        }

        private IEnumerable<LinkedChild> GetWplItems(Stream stream, string playlistPath, List<string> libraryRoots)
        {
            var content = new WplContent();
            var playlist = content.GetFromStream(stream);

            return playlist.PlaylistEntries
                    .Select(i => GetLinkedChild(i.Path, playlistPath, libraryRoots))
                    .Where(i => i is not null);
        }

        private LinkedChild GetLinkedChild(string itemPath, string playlistPath, List<string> libraryRoots)
        {
            if (TryGetPlaylistItemPath(itemPath, playlistPath, libraryRoots, out var parsedPath))
            {
                return new LinkedChild
                {
                    Path = parsedPath,
                    Type = LinkedChildType.Manual
                };
            }

            return null;
        }

        private bool TryGetPlaylistItemPath(string itemPath, string playlistPath, List<string> libraryPaths, out string path)
        {
            path = null;
            var baseFolder = Path.GetDirectoryName(playlistPath);
            var basePath = Path.Combine(baseFolder, itemPath);
            var fullPath = Path.GetFullPath(basePath);

            foreach (var libraryPath in libraryPaths)
            {
                if (fullPath.StartsWith(libraryPath, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                {
                    path = fullPath;
                    return true;
                }
            }

            return false;
        }

        public bool HasChanged(BaseItem item, IDirectoryService directoryService)
        {
            var path = item.Path;

            if (!string.IsNullOrWhiteSpace(path) && item.IsFileProtocol)
            {
                var file = directoryService.GetFile(path);
                if (file is not null && file.LastWriteTimeUtc != item.DateModified)
                {
                    _logger.LogDebug("Refreshing {Path} due to date modified timestamp change.", path);
                    return true;
                }
            }

            return false;
        }
    }
}
