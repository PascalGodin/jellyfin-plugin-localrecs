using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Manages the Leaving Soon and Removal Candidates item lists.
    /// Items with the lowest max-user cosine similarity are flagged as Leaving Soon.
    /// After a configurable dwell period they are promoted to Removal Candidates.
    /// Virtual library creation is handled separately; this service only scores and tracks state.
    /// </summary>
    public class LeavingSoonService
    {
        private readonly ILogger<LeavingSoonService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly string _stateFilePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="LeavingSoonService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="pluginDataPath">Plugin data directory path.</param>
        public LeavingSoonService(
            ILogger<LeavingSoonService> logger,
            ILibraryManager libraryManager,
            string pluginDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _stateFilePath = Path.Combine(
                pluginDataPath ?? throw new ArgumentNullException(nameof(pluginDataPath)),
                "leaving-soon-state.json");
        }

        /// <summary>
        /// Runs the full Leaving Soon refresh pipeline using pre-computed user profiles and embeddings.
        /// </summary>
        /// <param name="allItems">All media items in the library.</param>
        /// <param name="embeddings">Pre-computed item embeddings.</param>
        /// <param name="eligibleProfiles">Pre-computed taste profiles for users with enough watch history.</param>
        /// <param name="watchStatus">Aggregated watch status across all users, keyed by item ID.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Updated Leaving Soon state and discovery diagnostics after this run.</returns>
        public (LeavingSoonState State, LeavingSoonDiagnostics Diagnostics) Refresh(
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyList<UserProfile> eligibleProfiles,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (allItems == null)
            {
                throw new ArgumentNullException(nameof(allItems));
            }

            if (embeddings == null)
            {
                throw new ArgumentNullException(nameof(embeddings));
            }

            if (eligibleProfiles == null)
            {
                throw new ArgumentNullException(nameof(eligibleProfiles));
            }

            if (watchStatus == null)
            {
                throw new ArgumentNullException(nameof(watchStatus));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _logger.LogInformation("Starting Leaving Soon refresh");

            var state = LoadState();
            var allItemsById = BuildItemIndex(allItems);

            var now = DateTime.UtcNow;
            var minAge = TimeSpan.FromDays(config.LeavingSoonMinAgeDays);

            // Pass 1: Remove deleted, favorited, or too-young items from state
            Cleanup(state, allItemsById, watchStatus, minAge, now);
            cancellationToken.ThrowIfCancellationRequested();

            // Pass 2: Promote items that have exceeded the dwell period
            Promote(state, config);
            cancellationToken.ThrowIfCancellationRequested();

            // Pass 3: Discover new candidates using pre-computed embeddings and profiles
            LeavingSoonDiagnostics diagnostics;
            if (eligibleProfiles.Count > 0)
            {
                diagnostics = Discover(state, allItems, embeddings, eligibleProfiles, watchStatus, config, minAge, now);
            }
            else
            {
                _logger.LogDebug("Leaving Soon discovery skipped: no users have enough watch history");
                diagnostics = new LeavingSoonDiagnostics();
            }

            cancellationToken.ThrowIfCancellationRequested();

            SaveState(state);

            _logger.LogInformation(
                "Leaving Soon refresh complete: {FlaggedCount} flagged, {RemovalCount} removal candidates",
                state.FlaggedItems.Count,
                state.RemovalCandidates.Count);

            return (state, diagnostics);
        }

        private static Dictionary<string, MediaItemMetadata> BuildItemIndex(IReadOnlyList<MediaItemMetadata> allItems)
        {
            var index = new Dictionary<string, MediaItemMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in allItems)
            {
                index[item.Id.ToString()] = item;
            }

            return index;
        }

        private void Cleanup(
            LeavingSoonState state,
            Dictionary<string, MediaItemMetadata> allItemsById,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus,
            TimeSpan minAge,
            DateTime now)
        {
            var toRemove = new List<string>();
            var allStateIds = state.FlaggedItems.Keys.Concat(state.RemovalCandidates.Keys).Distinct().ToList();

            foreach (var id in allStateIds)
            {
                if (!allItemsById.TryGetValue(id, out var meta))
                {
                    toRemove.Add(id);
                    continue;
                }

                if (meta.Genres.Count == 0 && meta.Actors.Count == 0)
                {
                    toRemove.Add(id);
                    continue;
                }

                watchStatus.TryGetValue(meta.Id, out var ws);

                if (ws.IsAnyFavorite)
                {
                    toRemove.Add(id);
                    continue;
                }

                var item = _libraryManager.GetItemById(meta.Id);
                if (item == null)
                {
                    toRemove.Add(id);
                    continue;
                }

                var addedDate = item is Folder folder ? (folder.DateLastMediaAdded ?? folder.DateCreated) : item.DateCreated;
                var timeSinceAdded = now - addedDate;
                var effectiveAge = ws.LatestWatchDate.HasValue
                    ? TimeSpan.FromTicks(Math.Min(timeSinceAdded.Ticks, (now - ws.LatestWatchDate.Value).Ticks))
                    : timeSinceAdded;

                if (effectiveAge < minAge)
                {
                    toRemove.Add(id);
                }
            }

            foreach (var id in toRemove)
            {
                state.FlaggedItems.Remove(id);
                state.RemovalCandidates.Remove(id);
            }

            if (toRemove.Count > 0)
            {
                _logger.LogDebug("Leaving Soon cleanup: removed {Count} items from state", toRemove.Count);
            }
        }

        private void Promote(LeavingSoonState state, PluginConfiguration config)
        {
            var toPromote = state.FlaggedItems
                .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalDays >= config.LeavingSoonDwellDays)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in toPromote)
            {
                state.FlaggedItems.Remove(id);
                state.RemovalCandidates[id] = DateTime.UtcNow;
            }

            if (toPromote.Count > 0)
            {
                _logger.LogDebug("Leaving Soon promotion: moved {Count} items to Removal Candidates", toPromote.Count);
            }
        }

        private LeavingSoonDiagnostics Discover(
            LeavingSoonState state,
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyList<UserProfile> eligibleProfiles,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus,
            PluginConfiguration config,
            TimeSpan minAge,
            DateTime now)
        {
            var diag = new LeavingSoonDiagnostics
            {
                EligibleProfileCount = eligibleProfiles.Count
            };

            var scoredMovies = new List<(string Id, float Score)>();
            var scoredTv = new List<(string Id, float Score)>();

            foreach (var meta in allItems)
            {
                var id = meta.Id.ToString();

                if (state.RemovalCandidates.ContainsKey(id))
                {
                    diag.SkippedAlreadyRemoval++;
                    continue;
                }

                if (meta.Genres.Count == 0 && meta.Actors.Count == 0)
                {
                    diag.SkippedNoMetadata++;
                    continue;
                }

                if (!embeddings.TryGetValue(meta.Id, out var embedding))
                {
                    diag.SkippedNoEmbedding++;
                    continue;
                }

                if (!state.FlaggedItems.ContainsKey(id))
                {
                    watchStatus.TryGetValue(meta.Id, out var ws);

                    if (ws.IsAnyFavorite)
                    {
                        diag.SkippedAlwaysSafe++;
                        continue;
                    }

                    var item = _libraryManager.GetItemById(meta.Id);
                    if (item == null)
                    {
                        diag.SkippedNotFound++;
                        continue;
                    }

                    var addedDate = item is Folder folder ? (folder.DateLastMediaAdded ?? folder.DateCreated) : item.DateCreated;
                    var timeSinceAdded = now - addedDate;
                    var effectiveAge = ws.LatestWatchDate.HasValue
                        ? TimeSpan.FromTicks(Math.Min(timeSinceAdded.Ticks, (now - ws.LatestWatchDate.Value).Ticks))
                        : timeSinceAdded;

                    if (effectiveAge < minAge)
                    {
                        diag.SkippedTooYoung++;
                        continue;
                    }
                }

                var maxSimilarity = 0f;
                foreach (var profile in eligibleProfiles)
                {
                    var sim = VectorMath.CosineSimilarity(profile.TasteVector, embedding.Vector);
                    if (sim > maxSimilarity)
                    {
                        maxSimilarity = sim;
                    }
                }

                if (meta.Type == MediaType.Movie)
                {
                    diag.ScoredMovies++;
                    scoredMovies.Add((id, maxSimilarity));
                }
                else if (meta.Type == MediaType.Series)
                {
                    diag.ScoredTv++;
                    scoredTv.Add((id, maxSimilarity));
                }
            }

            var targetMovies = scoredMovies
                .OrderBy(x => x.Score)
                .Take(config.LeavingSoonMovieCount)
                .Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var targetTv = scoredTv
                .OrderBy(x => x.Score)
                .Take(config.LeavingSoonTvCount)
                .Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var scoredIds = new HashSet<string>(
                scoredMovies.Select(x => x.Id).Concat(scoredTv.Select(x => x.Id)),
                StringComparer.OrdinalIgnoreCase);

            var evicted = state.FlaggedItems.Keys
                .Where(id => scoredIds.Contains(id) && !targetMovies.Contains(id) && !targetTv.Contains(id))
                .ToList();

            foreach (var id in evicted)
            {
                state.FlaggedItems.Remove(id);
            }

            var flagged = 0;
            foreach (var id in targetMovies.Concat(targetTv))
            {
                if (!state.FlaggedItems.ContainsKey(id))
                {
                    state.FlaggedItems[id] = DateTime.UtcNow;
                    flagged++;
                }
            }

            _logger.LogInformation(
                "Leaving Soon discovery: {MovieCandidates} movie candidates, {TvCandidates} TV candidates — flagged {Flagged} new, evicted {Evicted} (total flagged: {FlaggedTotal})",
                scoredMovies.Count,
                scoredTv.Count,
                flagged,
                evicted.Count,
                state.FlaggedItems.Count);

            return diag;
        }

        private LeavingSoonState LoadState()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                {
                    return new LeavingSoonState();
                }

                var json = File.ReadAllText(_stateFilePath);
                return JsonSerializer.Deserialize<LeavingSoonState>(json) ?? new LeavingSoonState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Leaving Soon state from {Path}; starting fresh", _stateFilePath);
                return new LeavingSoonState();
            }
        }

        private void SaveState(LeavingSoonState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Leaving Soon state to {Path}", _stateFilePath);
            }
        }
    }
}
