using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.LocalRecs.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Manages virtual library symlinks for per-user recommendations.
    /// Creates filesystem symlinks in per-user directories that point to original media files.
    /// Symlinks replace the prior .strm approach, which broke in Jellyfin 10.11.7 when
    /// the server stopped accepting local paths in .strm files (security fix GHSA-j2hf-x4q5-47j3).
    /// </summary>
    public class VirtualLibraryManager
    {
        private readonly ILogger<VirtualLibraryManager> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly string _virtualLibraryBasePath;

        private readonly ConcurrentDictionary<Guid, object> _userLocks = new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualLibraryManager"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager for media access.</param>
        /// <param name="virtualLibraryBasePath">Base path for virtual libraries.</param>
        public VirtualLibraryManager(
            ILogger<VirtualLibraryManager> logger,
            ILibraryManager libraryManager,
            string virtualLibraryBasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _virtualLibraryBasePath = virtualLibraryBasePath ?? throw new ArgumentNullException(nameof(virtualLibraryBasePath));
        }

        /// <summary>Gets the path for the Leaving Soon Movies shared library.</summary>
        public string LeavingSoonMoviesPath => Path.Combine(_virtualLibraryBasePath, "leaving-soon", "movies");

        /// <summary>Gets the path for the Leaving Soon TV shared library.</summary>
        public string LeavingSoonTvPath => Path.Combine(_virtualLibraryBasePath, "leaving-soon", "tv");

        /// <summary>Gets the path for the Removal Candidates Movies shared library (admin only).</summary>
        public string RemovalCandidatesMoviesPath => Path.Combine(_virtualLibraryBasePath, "leaving-soon", "removal-movies");

        /// <summary>Gets the path for the Removal Candidates TV shared library (admin only).</summary>
        public string RemovalCandidatesTvPath => Path.Combine(_virtualLibraryBasePath, "leaving-soon", "removal-tv");

        /// <summary>
        /// Gets the virtual library path for a specific user and media type.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="mediaType">Media type (Movie or Series).</param>
        /// <returns>Full path to the user's virtual library directory.</returns>
        public string GetUserLibraryPath(Guid userId, MediaType mediaType)
        {
            var subfolder = mediaType == MediaType.Movie ? "movies" : "tv";
            return Path.Combine(_virtualLibraryBasePath, userId.ToString(), subfolder);
        }

        /// <summary>
        /// Ensures the virtual library directories exist for a user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="username">Username for logging purposes.</param>
        /// <returns>True if directories were created successfully, false otherwise.</returns>
        public bool EnsureUserDirectoriesExist(Guid userId, string? username = null)
        {
            var displayName = username ?? userId.ToString();

            try
            {
                Directory.CreateDirectory(GetUserLibraryPath(userId, MediaType.Movie));
                Directory.CreateDirectory(GetUserLibraryPath(userId, MediaType.Series));

                _logger.LogDebug(
                    "Ensured virtual library directories exist for user {Username} ({UserId})",
                    displayName,
                    userId);

                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to create directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied creating directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
        }

        /// <summary>
        /// Deletes all virtual library directories for a user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="username">Username for logging purposes.</param>
        /// <returns>True if directories were deleted or didn't exist, false on error.</returns>
        public bool DeleteUserDirectories(Guid userId, string? username = null)
        {
            var displayName = username ?? userId.ToString();
            var userPath = Path.Combine(_virtualLibraryBasePath, userId.ToString());

            if (!Directory.Exists(userPath))
            {
                return true;
            }

            try
            {
                Directory.Delete(userPath, recursive: true);
                _logger.LogDebug(
                    "Deleted virtual library directories for user {Username} ({UserId})",
                    displayName,
                    userId);
                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied deleting directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
        }

        /// <summary>
        /// Clears and recreates recommendations for a user. Thread-safe per user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="recommendations">List of recommended items.</param>
        /// <param name="mediaType">Media type (Movie or Series).</param>
        /// <returns>Number of items created.</returns>
        /// <exception cref="ArgumentNullException">Thrown when recommendations is null.</exception>
        public int SyncRecommendations(
            Guid userId,
            IReadOnlyList<ScoredRecommendation> recommendations,
            MediaType mediaType)
        {
            if (recommendations == null)
            {
                throw new ArgumentNullException(nameof(recommendations));
            }

            var userLock = _userLocks.GetOrAdd(userId, _ => new object());
            lock (userLock)
            {
                return SyncRecommendationsInternal(userId, recommendations, mediaType);
            }
        }

        /// <summary>
        /// Clears and recreates all four Leaving Soon / Removal Candidates shared libraries.
        /// </summary>
        /// <param name="state">The current leaving-soon state.</param>
        /// <param name="allItems">All media items (used to determine media type per item).</param>
        public void SyncLeavingSoon(LeavingSoonState state, IReadOnlyList<MediaItemMetadata> allItems)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var metaById = new Dictionary<string, MediaItemMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in allItems)
            {
                metaById[m.Id.ToString()] = m;
            }

            SyncSharedLibrary(LeavingSoonMoviesPath, FilterIds(state.FlaggedItems, metaById, MediaType.Movie));
            SyncSharedLibrary(LeavingSoonTvPath, FilterIds(state.FlaggedItems, metaById, MediaType.Series));
            SyncSharedLibrary(RemovalCandidatesMoviesPath, FilterIds(state.RemovalCandidates, metaById, MediaType.Movie));
            SyncSharedLibrary(RemovalCandidatesTvPath, FilterIds(state.RemovalCandidates, metaById, MediaType.Series));

            _logger.LogDebug(
                "Synced Leaving Soon libraries: {FlaggedCount} flagged, {RemovalCount} removal candidates",
                state.FlaggedItems.Count,
                state.RemovalCandidates.Count);
        }

        private static IEnumerable<Guid> FilterIds(
            Dictionary<string, DateTime> stateDict,
            Dictionary<string, MediaItemMetadata> metaById,
            MediaType mediaType)
        {
            foreach (var id in stateDict.Keys)
            {
                if (metaById.TryGetValue(id, out var meta) && meta.Type == mediaType)
                {
                    yield return meta.Id;
                }
            }
        }

        private void SyncSharedLibrary(string libraryPath, IEnumerable<Guid> itemIds)
        {
            if (Directory.Exists(libraryPath))
            {
                Directory.Delete(libraryPath, recursive: true);
            }

            Directory.CreateDirectory(libraryPath);

            foreach (var itemId in itemIds)
            {
                try
                {
                    var item = _libraryManager.GetItemById(itemId);
                    if (item == null || string.IsNullOrEmpty(item.Path))
                    {
                        continue;
                    }

                    if (item is Series series)
                    {
                        CreateSeriesStructure(libraryPath, series);
                    }
                    else if (item is Episode || item is Season)
                    {
                        // Stale state entry: the Guid was reassigned to a TV sub-item after a
                        // library re-index. Skip to avoid orphaned episode cards in the library.
                        // Cleanup will purge the stale ID on the next refresh cycle.
                        _logger.LogWarning(
                            "Leaving Soon: item {ItemId} resolved to {ItemType} instead of Series or Movie; skipping",
                            itemId,
                            item.GetType().Name);
                    }
                    else
                    {
                        CreateMovieFolderStructure(libraryPath, item);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogSymlinkPermissionError(ex, itemId);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to create leaving-soon entry for item {ItemId} (IO error)", itemId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create leaving-soon entry for item {ItemId}", itemId);
                }
            }
        }

        private int SyncRecommendationsInternal(
            Guid userId,
            IReadOnlyList<ScoredRecommendation> recommendations,
            MediaType mediaType)
        {
            var libraryPath = GetUserLibraryPath(userId, mediaType);

            ClearRecommendationsInternal(userId, mediaType);
            Directory.CreateDirectory(libraryPath);

            var createdCount = 0;
            foreach (var rec in recommendations)
            {
                try
                {
                    var item = _libraryManager.GetItemById(rec.ItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("Item {ItemId} not found in library", rec.ItemId);
                        continue;
                    }

                    if (string.IsNullOrEmpty(item.Path))
                    {
                        _logger.LogDebug("Item {ItemId} ({ItemName}) has no path, skipping", rec.ItemId, item.Name);
                        continue;
                    }

                    if (item is Series series)
                    {
                        CreateSeriesStructure(libraryPath, series);
                    }
                    else
                    {
                        CreateMovieFolderStructure(libraryPath, item);
                    }

                    createdCount++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogSymlinkPermissionError(ex, rec.ItemId);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to create virtual library entry for item {ItemId} (IO error)", rec.ItemId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create virtual library entry for item {ItemId}", rec.ItemId);
                }
            }

            _logger.LogDebug(
                "Updated {MediaType} recommendations for user {UserId}: {Created} items created",
                mediaType,
                userId,
                createdCount);

            return createdCount;
        }

        private void ClearRecommendationsInternal(Guid userId, MediaType mediaType)
        {
            var libraryPath = GetUserLibraryPath(userId, mediaType);

            if (!Directory.Exists(libraryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(libraryPath, recursive: true);
                _logger.LogDebug(
                    "Cleared all {MediaType} items for user {UserId}",
                    mediaType,
                    userId);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete virtual library directory (IO error): {Path}", libraryPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Failed to delete virtual library directory (access denied): {Path}", libraryPath);
            }
        }

        private void CreateMovieFolderStructure(string libraryPath, BaseItem item)
        {
            var sourceFolder = Path.GetDirectoryName(item.Path);
            if (string.IsNullOrEmpty(sourceFolder))
            {
                _logger.LogDebug("Movie {ItemName} has no parent folder, skipping", item.Name);
                return;
            }

            // Symlink the entire source movie folder rather than a renamed file symlink.
            // File symlinks are resolved by Jellyfin's scanner to the source file path, which
            // causes virtual items to be deduplicated with the source library items and played
            // back via the source item's path — breaking playback from the virtual library.
            // A directory symlink keeps the virtual path intact; the OS resolves it at stream time.
            var folderName = GenerateFolderName(item);
            var linkPath = Path.Combine(libraryPath, folderName);
            Directory.CreateSymbolicLink(linkPath, sourceFolder);

            _logger.LogDebug(
                "Created movie folder symlink: {FolderName} -> {SourcePath}",
                folderName,
                sourceFolder);
        }

        private void CreateSeriesStructure(string libraryPath, Series series)
        {
            if (string.IsNullOrEmpty(series.Path))
            {
                _logger.LogDebug("Series {SeriesName} has no path, skipping", series.Name);
                return;
            }

            // Symlink the entire source series folder rather than recreating per-episode
            // symlinks. This ensures all episodes (including those added after the last refresh)
            // are visible, avoids filename-collision bugs, and avoids the episode-deduplication
            // issue where Jellyfin matches individual episode symlinks back to source library
            // items and displays them as orphaned Episode cards instead of Series tiles.
            var seriesFolderName = GenerateFolderName(series);
            var linkPath = Path.Combine(libraryPath, seriesFolderName);
            Directory.CreateSymbolicLink(linkPath, series.Path);

            _logger.LogDebug(
                "Created series folder symlink: {FolderName} -> {SourcePath}",
                seriesFolderName,
                series.Path);
        }

        private void LogSymlinkPermissionError(UnauthorizedAccessException ex, Guid itemId)
        {
            const string Msg = "Access denied creating symlink for item {ItemId}. On Windows, Jellyfin must run as Administrator or the host must have Developer Mode enabled (Settings > Privacy & security > For developers). See README troubleshooting section.";
            _logger.LogError(ex, Msg, itemId);
        }

        private string GenerateFolderName(BaseItem item)
        {
            var title = SanitizeFilename(item.Name ?? "Unknown");
            var year = item.ProductionYear ?? 0;
            var providerId = GetProviderId(item);

            return year > 0
                ? $"{title} ({year}) [{providerId}]"
                : $"{title} [{providerId}]";
        }

        private string GetProviderId(BaseItem item)
        {
            var providerIds = item.ProviderIds ?? new Dictionary<string, string>();

            if (providerIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
            {
                return $"tmdbid-{tmdbId}";
            }

            if (providerIds.TryGetValue("Tvdb", out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
            {
                return $"tvdbid-{tvdbId}";
            }

            return $"jellyfinid-{item.Id}";
        }

        private string SanitizeFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return "Unknown";
            }

            var invalidChars = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            var sanitized = string.Join("_", filename.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            sanitized = sanitized.Replace("..", "_").Replace("/", "_").Replace("\\", "_");
            sanitized = sanitized.TrimStart('.', '-').TrimEnd();

            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
        }
    }
}
