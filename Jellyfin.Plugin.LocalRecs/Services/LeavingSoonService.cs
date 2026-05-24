using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

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
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly string _stateFilePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="LeavingSoonService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="userDataManager">User data manager.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="pluginDataPath">Plugin data directory path.</param>
        public LeavingSoonService(
            ILogger<LeavingSoonService> logger,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            string pluginDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
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
        /// <param name="config">Plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Updated Leaving Soon state after this run.</returns>
        public LeavingSoonState Refresh(
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyList<UserProfile> eligibleProfiles,
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

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _logger.LogInformation("Starting Leaving Soon refresh");

            var state = LoadState();
            var users = _userManager.GetUsers().ToList();
            var allItemsById = BuildItemIndex(allItems);

            var now = DateTime.UtcNow;
            var minAge = TimeSpan.FromDays(config.LeavingSoonMinAgeDays);

            // Pass 1: Remove saved, deleted, or collection-protected items
            var safeCollections = BuildSafeCollections(allItems, users);
            Cleanup(state, allItemsById, users, safeCollections, minAge, now);
            cancellationToken.ThrowIfCancellationRequested();

            // Pass 2: Promote items that have exceeded the dwell period
            Promote(state, config);
            cancellationToken.ThrowIfCancellationRequested();

            // Pass 3: Discover new candidates using pre-computed embeddings and profiles
            if (eligibleProfiles.Count > 0)
            {
                Discover(state, allItems, embeddings, eligibleProfiles, safeCollections, users, config, minAge, now);
            }
            else
            {
                _logger.LogDebug("Leaving Soon discovery skipped: no users have enough watch history");
            }

            cancellationToken.ThrowIfCancellationRequested();

            SaveState(state);

            _logger.LogInformation(
                "Leaving Soon refresh complete: {FlaggedCount} flagged, {RemovalCount} removal candidates",
                state.FlaggedItems.Count,
                state.RemovalCandidates.Count);

            return state;
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

        private HashSet<string> BuildSafeCollections(
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyList<User> users)
        {
            var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var meta in allItems)
            {
                if (string.IsNullOrWhiteSpace(meta.CollectionName))
                {
                    continue;
                }

                if (safe.Contains(meta.CollectionName))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(meta.Id);
                if (item == null)
                {
                    continue;
                }

                foreach (var user in users)
                {
                    if (IsItemSafeForUser(item, user))
                    {
                        safe.Add(meta.CollectionName);
                        break;
                    }
                }
            }

            _logger.LogDebug("Built safe collection set: {Count} protected collections", safe.Count);
            return safe;
        }

        private void Cleanup(
            LeavingSoonState state,
            Dictionary<string, MediaItemMetadata> allItemsById,
            IReadOnlyList<User> users,
            HashSet<string> safeCollections,
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

                if (!string.IsNullOrWhiteSpace(meta.CollectionName) && safeCollections.Contains(meta.CollectionName))
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

                var alwaysSafe = false;
                foreach (var user in users)
                {
                    var ud = _userDataManager.GetUserData(user, item);
                    if (ud != null && (ud.IsFavorite || ud.PlaybackPositionTicks > 0))
                    {
                        alwaysSafe = true;
                        break;
                    }
                }

                if (alwaysSafe)
                {
                    toRemove.Add(id);
                    continue;
                }

                var addedDate = item is Folder folder ? (folder.DateLastMediaAdded ?? folder.DateCreated) : item.DateCreated;
                var timeSinceAdded = now - addedDate;
                DateTime? latestWatchDate = null;
                foreach (var user in users)
                {
                    var wd = GetLastWatchDate(item, user);
                    if (wd.HasValue && (latestWatchDate == null || wd.Value > latestWatchDate.Value))
                    {
                        latestWatchDate = wd.Value;
                    }
                }

                var effectiveAge = latestWatchDate.HasValue
                    ? TimeSpan.FromTicks(Math.Min(timeSinceAdded.Ticks, (now - latestWatchDate.Value).Ticks))
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

        private void Discover(
            LeavingSoonState state,
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyList<UserProfile> eligibleProfiles,
            HashSet<string> safeCollections,
            IReadOnlyList<User> users,
            PluginConfiguration config,
            TimeSpan minAge,
            DateTime now)
        {
            var scoredMovies = new List<(string Id, float Score)>();
            var scoredTv = new List<(string Id, float Score)>();

            foreach (var meta in allItems)
            {
                var id = meta.Id.ToString();

                if (state.RemovalCandidates.ContainsKey(id))
                {
                    continue;
                }

                if (meta.Genres.Count == 0 && meta.Actors.Count == 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(meta.CollectionName) && safeCollections.Contains(meta.CollectionName))
                {
                    continue;
                }

                if (!embeddings.TryGetValue(meta.Id, out var embedding))
                {
                    continue;
                }

                if (!state.FlaggedItems.ContainsKey(id))
                {
                    var item = _libraryManager.GetItemById(meta.Id);
                    if (item == null)
                    {
                        continue;
                    }

                    var alwaysSafe = false;
                    foreach (var user in users)
                    {
                        var ud = _userDataManager.GetUserData(user, item);
                        if (ud != null && (ud.IsFavorite || ud.PlaybackPositionTicks > 0))
                        {
                            alwaysSafe = true;
                            break;
                        }
                    }

                    if (alwaysSafe)
                    {
                        continue;
                    }

                    var addedDate = item is Folder folder ? (folder.DateLastMediaAdded ?? folder.DateCreated) : item.DateCreated;
                    var timeSinceAdded = now - addedDate;

                    DateTime? latestWatchDate = null;
                    foreach (var user in users)
                    {
                        var watchDate = GetLastWatchDate(item, user);
                        if (watchDate.HasValue && (latestWatchDate == null || watchDate.Value > latestWatchDate.Value))
                        {
                            latestWatchDate = watchDate.Value;
                        }
                    }

                    var effectiveAge = latestWatchDate.HasValue
                        ? TimeSpan.FromTicks(Math.Min(timeSinceAdded.Ticks, (now - latestWatchDate.Value).Ticks))
                        : timeSinceAdded;

                    if (effectiveAge < minAge)
                    {
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
                    scoredMovies.Add((id, maxSimilarity));
                }
                else if (meta.Type == MediaType.Series)
                {
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
        }

        private bool IsItemSafeForUser(BaseItem item, User user)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null)
            {
                return false;
            }

            if (userData.Played || userData.IsFavorite || userData.PlaybackPositionTicks > 0)
            {
                return true;
            }

            if (item is Series series)
            {
                return HasAnyWatchedEpisode(series, user);
            }

            return false;
        }

        private bool HasAnyWatchedEpisode(Series series, User user)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            });

            foreach (var ep in episodes)
            {
                var epData = _userDataManager.GetUserData(user, ep);
                if (epData != null && (epData.Played || epData.PlaybackPositionTicks > 0))
                {
                    return true;
                }
            }

            return false;
        }

        private DateTime? GetLastWatchDate(BaseItem item, User user)
        {
            return _userDataManager.GetUserData(user, item)?.LastPlayedDate;
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
