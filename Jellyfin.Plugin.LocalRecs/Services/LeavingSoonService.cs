using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Manages the Leaving Soon and Removal Candidates virtual libraries.
    /// Items with the lowest similarity to any user's taste vector are flagged as Leaving Soon.
    /// After a configurable dwell period, they are promoted to Removal Candidates.
    /// </summary>
    public class LeavingSoonService
    {
        private readonly ILogger<LeavingSoonService> _logger;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILibraryManager _libraryManager;
        private readonly UserProfileService _userProfileService;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly string _stateFilePath;

        /// <summary>
        /// Initializes a new instance of the <see cref="LeavingSoonService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="userDataManager">User data manager.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="userProfileService">User profile service.</param>
        /// <param name="virtualLibraryManager">Virtual library manager.</param>
        /// <param name="pluginDataPath">Plugin data directory path.</param>
        public LeavingSoonService(
            ILogger<LeavingSoonService> logger,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILibraryManager libraryManager,
            UserProfileService userProfileService,
            VirtualLibraryManager virtualLibraryManager,
            string pluginDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
            _stateFilePath = Path.Combine(
                pluginDataPath ?? throw new ArgumentNullException(nameof(pluginDataPath)),
                "leaving-soon-state.json");
        }

        /// <summary>
        /// Runs the full Leaving Soon refresh pipeline.
        /// Uses pre-computed embeddings to avoid recomputing them.
        /// </summary>
        /// <param name="allItems">All media items in the library.</param>
        /// <param name="embeddings">Pre-computed item embeddings.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the async operation.</returns>
        public Task RefreshAsync(
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
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

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _logger.LogInformation("Starting Leaving Soon refresh");

            var state = LoadState();
            var users = _userManager.Users.ToList();
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

            // Pass 3: Discover new candidates using pre-computed embeddings
            var eligibleProfiles = BuildEligibleProfiles(users, embeddings, config);
            if (eligibleProfiles.Count > 0)
            {
                Discover(state, allItems, embeddings, eligibleProfiles, safeCollections, users, config, minAge, now);
            }
            else
            {
                _logger.LogDebug("Leaving Soon discovery skipped: no users have enough watch history");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Pass 4: Persist state and sync virtual library symlinks
            SaveState(state);
            _virtualLibraryManager.SyncLeavingSoon(state, allItems);

            _logger.LogInformation(
                "Leaving Soon refresh complete: {FlaggedCount} flagged, {RemovalCount} removal candidates",
                state.FlaggedItems.Count,
                state.RemovalCandidates.Count);

            return Task.CompletedTask;
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

        /// <summary>
        /// Builds the set of collection names that are "safe" — at least one member has been
        /// played, favourited, or has playback progress for any user.
        /// No item belonging to a safe collection will be flagged as Leaving Soon.
        /// </summary>
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

        /// <summary>
        /// Pass 1: Remove items from state that are no longer candidates.
        /// Removes items that were deleted, saved by any user, or protected by a safe collection sibling.
        /// </summary>
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
                // Item deleted from library
                if (!allItemsById.TryGetValue(id, out var meta))
                {
                    toRemove.Add(id);
                    continue;
                }

                // Metadata-poor items (same filter as discovery)
                if (meta.Genres.Count == 0 && meta.Actors.Count == 0)
                {
                    toRemove.Add(id);
                    continue;
                }

                // Collection sibling protection
                if (!string.IsNullOrWhiteSpace(meta.CollectionName) && safeCollections.Contains(meta.CollectionName))
                {
                    toRemove.Add(id);
                    continue;
                }

                // Saved by any user
                var item = _libraryManager.GetItemById(meta.Id);
                if (item == null)
                {
                    toRemove.Add(id);
                    continue;
                }

                // Favorites and in-progress items are always protected from leaving soon.
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

                // Effective age = min(time since added, time since last watched by any user).
                // Remove from state if too recent — mirrors the Discover eligibility check.
                var timeSinceAdded = now - item.DateCreated;
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

        /// <summary>
        /// Pass 2: Promote items that have been in Leaving Soon beyond the dwell period.
        /// </summary>
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

        /// <summary>
        /// Pass 3: Score all eligible candidates and flag the lowest-scoring ones.
        /// </summary>
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

                // Items already promoted to removal candidates are done — never re-flag
                if (state.RemovalCandidates.ContainsKey(id))
                {
                    continue;
                }

                // Metadata-poor items
                if (meta.Genres.Count == 0 && meta.Actors.Count == 0)
                {
                    continue;
                }

                // Collection-protected
                if (!string.IsNullOrWhiteSpace(meta.CollectionName) && safeCollections.Contains(meta.CollectionName))
                {
                    continue;
                }

                // Must have an embedding to score
                if (!embeddings.TryGetValue(meta.Id, out var embedding))
                {
                    continue;
                }

                // Age and interaction checks apply only to new candidates.
                // Items already in FlaggedItems passed these checks when first flagged,
                // and the Cleanup pass has already removed any that were interacted with.
                if (!state.FlaggedItems.ContainsKey(id))
                {
                    var item = _libraryManager.GetItemById(meta.Id);
                    if (item == null)
                    {
                        continue;
                    }

                    // Favorites and in-progress items are always protected.
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

                    // Effective age = min(time since added to library, time since last watched by any user).
                    // Items recently added OR recently watched by any user are skipped.
                    var timeSinceAdded = now - item.DateCreated;

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

                // Score: max cosine similarity across all eligible user taste vectors
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

            // Compute the target sets: bottom N total (lowest similarity = least likely to be watched).
            // This is authoritative — if the configured count decreased, excess items are evicted.
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

            // All IDs that were scored (have embeddings and pass filters)
            var scoredIds = new HashSet<string>(
                scoredMovies.Select(x => x.Id).Concat(scoredTv.Select(x => x.Id)),
                StringComparer.OrdinalIgnoreCase);

            // Remove flagged items that were scored but fell outside the target set.
            // Items with no embedding are left untouched (they'll be cleaned up next cycle
            // if they remain invalid, or scored once their embedding is computed).
            var evicted = state.FlaggedItems.Keys
                .Where(id => scoredIds.Contains(id) && !targetMovies.Contains(id) && !targetTv.Contains(id))
                .ToList();

            foreach (var id in evicted)
            {
                state.FlaggedItems.Remove(id);
            }

            // Flag newly-selected items
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

        /// <summary>
        /// Builds taste profiles for all users who have enough watch history.
        /// </summary>
        private IReadOnlyList<UserProfile> BuildEligibleProfiles(
            IReadOnlyList<User> users,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            PluginConfiguration config)
        {
            if (embeddings.Count == 0)
            {
                return Array.Empty<UserProfile>();
            }

            var profiles = new List<UserProfile>();

            foreach (var user in users)
            {
                var profile = _userProfileService.BuildUserProfile(user.Id, embeddings, config);
                if (profile != null && profile.WatchedItemCount >= config.MinWatchedItemsForPersonalization)
                {
                    profiles.Add(profile);
                }
            }

            _logger.LogDebug(
                "Leaving Soon: {Eligible}/{Total} users have enough watch history for scoring",
                profiles.Count,
                users.Count);

            return profiles;
        }

        /// <summary>
        /// Returns true if the item has been played, favourited, or has playback progress for the given user.
        /// For series, also checks whether any episode has been watched.
        /// </summary>
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

            // For series, Played is only true when ALL episodes are watched.
            // Check for any watched or in-progress episode.
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

        /// <summary>
        /// Returns the most recent date the item was last played by the given user.
        /// For series, falls back to checking episode-level play dates.
        /// </summary>
        private DateTime? GetLastWatchDate(BaseItem item, User user)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData?.LastPlayedDate != null)
            {
                return userData.LastPlayedDate;
            }

            if (item is Series series)
            {
                return GetSeriesLastWatchDate(series, user);
            }

            return null;
        }

        private DateTime? GetSeriesLastWatchDate(Series series, User user)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            });

            DateTime? latest = null;
            foreach (var ep in episodes)
            {
                var epData = _userDataManager.GetUserData(user, ep);
                if (epData?.LastPlayedDate != null)
                {
                    if (latest == null || epData.LastPlayedDate.Value > latest.Value)
                    {
                        latest = epData.LastPlayedDate.Value;
                    }
                }
            }

            return latest;
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
