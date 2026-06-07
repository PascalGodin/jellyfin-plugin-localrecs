using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Service for building user taste profiles from watch history.
    /// Aggregates weighted embeddings into normalized taste vectors.
    /// </summary>
    public class UserProfileService
    {
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<UserProfileService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserProfileService"/> class.
        /// </summary>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger.</param>
        public UserProfileService(
            IUserDataManager userDataManager,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger<UserProfileService> logger)
        {
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Builds a user profile from watch history and embeddings.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="embeddings">Dictionary of item embeddings.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>UserProfile with taste vector (or null if no watch history), and the raw watch records used to build it.</returns>
        /// <exception cref="ArgumentNullException">Thrown when embeddings or config is null.</exception>
        /// <exception cref="ArgumentException">Thrown when embeddings dictionary is empty.</exception>
        public (UserProfile? Profile, IReadOnlyList<WatchRecord> WatchRecords) BuildUserProfile(
            Guid userId,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            PluginConfiguration config)
        {
            if (embeddings == null)
            {
                throw new ArgumentNullException(nameof(embeddings));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (embeddings.Count == 0)
            {
                throw new ArgumentException("Embeddings dictionary cannot be empty", nameof(embeddings));
            }

            _logger.LogDebug("Building user profile for user {UserId}", userId);

            // Get watch records for this user
            var watchRecords = GetWatchRecords(userId, embeddings.Keys);

            if (watchRecords.Count == 0)
            {
                _logger.LogWarning("No watch history found for user {UserId}", userId);
                return (null, Array.Empty<WatchRecord>());
            }

            _logger.LogDebug("Found {Count} watched items for user {UserId}", watchRecords.Count, userId);

            // Compute weighted taste vector
            var (tasteVector, contributions) = ComputeTasteVector(watchRecords, embeddings, config);

            // Compute rating statistics from watched items
            var (avgCommunity, avgCritic, communityStdDev, criticStdDev) = ComputeRatingStatistics(watchRecords);

            var profile = new UserProfile(userId, tasteVector)
            {
                WatchedItemCount = watchRecords.Count,
                AverageCommunityRating = avgCommunity,
                AverageCriticRating = avgCritic,
                CommunityRatingStdDev = communityStdDev,
                CriticRatingStdDev = criticStdDev,
                TopWatchContributions = contributions
                    .OrderByDescending(c => c.Weight)
                    .Take(10)
                    .ToList()
            };

            _logger.LogDebug(
                "Built profile for user {UserId}: {Count} items, AvgCommunity={AvgCommunity:F2}, AvgCritic={AvgCritic:F2}",
                userId,
                watchRecords.Count,
                avgCommunity ?? 0,
                avgCritic ?? 0);

            return (profile, watchRecords);
        }

        /// <summary>
        /// Returns the protected status of items that are favorited or in-progress for the given user.
        /// Only items where at least one flag is true are included.
        /// Used by Leaving Soon: favorited items are permanently gated; in-progress items are not gated
        /// but their LastPlayedDate is returned so the effective-age calculation can use the real last-touched
        /// timestamp rather than the item's library-add date.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="candidateItemIds">Item IDs to check (from embeddings).</param>
        /// <returns>Per-item status: IsFavorite flag and LastPlayedDate (set when in-progress, null otherwise).</returns>
        public IReadOnlyList<(Guid Id, bool IsFavorite, DateTime? LastPlayedDate)> GetProtectedItemStatuses(Guid userId, IEnumerable<Guid> candidateItemIds)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return Array.Empty<(Guid, bool, DateTime?)>();
            }

            var result = new List<(Guid, bool, DateTime?)>();
            foreach (var itemId in candidateItemIds)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    continue;
                }

                var userData = _userDataManager.GetUserData(user, item);
                if (userData == null)
                {
                    continue;
                }

                var isFavorite = userData.IsFavorite;
                var lastPlayedDate = (userData.PlaybackPositionTicks > 0 && !userData.Played)
                    ? userData.LastPlayedDate
                    : null;

                if (isFavorite || lastPlayedDate.HasValue)
                {
                    result.Add((itemId, isFavorite, lastPlayedDate));
                }
            }

            return result;
        }

        /// <summary>
        /// Checks virtual Leaving Soon library items for protection flags and maps them back to actual item IDs.
        /// Handles the case where a user favorites a series in the virtual Leaving Soon library but the flag was
        /// not synced to the source item. Series items are folders (not symlinks), so PlayStatusSyncService
        /// cannot resolve them and the sync is silently skipped.
        /// </summary>
        /// <param name="userIds">User IDs to check.</param>
        /// <param name="leavingSoonPaths">Filesystem paths of the Leaving Soon virtual libraries to scan.</param>
        /// <returns>Actual item IDs where any user has the virtual item favorited, OR-aggregated across all users.</returns>
        public IReadOnlyList<(Guid ActualItemId, bool IsFavorite)> GetVirtualLeavingSoonProtectedStatuses(
            IReadOnlyList<Guid> userIds,
            IEnumerable<string> leavingSoonPaths)
        {
            var users = userIds
                .Select(id => _userManager.GetUserById(id))
                .Where(u => u != null)
                .Select(u => u!)
                .ToList();

            if (users.Count == 0)
            {
                return Array.Empty<(Guid, bool)>();
            }

            var result = new HashSet<Guid>();

            foreach (var libraryPath in leavingSoonPaths)
            {
                if (!Directory.Exists(libraryPath))
                {
                    continue;
                }

                foreach (var linkPath in Directory.EnumerateFiles(libraryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var target = new FileInfo(linkPath).ResolveLinkTarget(returnFinalTarget: true);
                        if (target == null)
                        {
                            continue;
                        }

                        var sourceItem = _libraryManager.FindByPath(target.FullName, isFolder: false);
                        var virtualItem = _libraryManager.FindByPath(linkPath, isFolder: false);
                        if (sourceItem == null || virtualItem == null)
                        {
                            continue;
                        }

                        Guid actualItemId;
                        BaseItem? virtualSeriesItem = null;

                        if (virtualItem is Episode virtualEpisode && sourceItem is Episode sourceEpisode)
                        {
                            actualItemId = sourceEpisode.SeriesId;
                            if (virtualEpisode.SeriesId != Guid.Empty)
                            {
                                virtualSeriesItem = _libraryManager.GetItemById(virtualEpisode.SeriesId);
                            }
                        }
                        else
                        {
                            actualItemId = sourceItem.Id;
                        }

                        if (actualItemId == Guid.Empty)
                        {
                            continue;
                        }

                        var isFavorite = false;

                        foreach (var user in users)
                        {
                            var virtualUserData = _userDataManager.GetUserData(user, virtualItem);
                            if (virtualUserData != null)
                            {
                                isFavorite |= virtualUserData.IsFavorite;
                            }

                            if (!isFavorite && virtualSeriesItem != null)
                            {
                                var seriesUserData = _userDataManager.GetUserData(user, virtualSeriesItem);
                                if (seriesUserData != null)
                                {
                                    isFavorite |= seriesUserData.IsFavorite;
                                }
                            }
                        }

                        if (isFavorite)
                        {
                            result.Add(actualItemId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to check virtual protection status for: {Path}", linkPath);
                    }
                }
            }

            return result.Select(id => (id, true)).ToList();
        }

        /// <summary>
        /// Gets watch records for a user.
        /// For movies, includes items that have been fully watched (Played = true).
        /// For series, includes items with any watched episodes to capture partial engagement.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="availableItemIds">Available item IDs (from embeddings).</param>
        /// <returns>List of watch records.</returns>
        private List<WatchRecord> GetWatchRecords(Guid userId, IEnumerable<Guid> availableItemIds)
        {
            var records = new List<WatchRecord>();
            var user = _userManager.GetUserById(userId);

            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return records;
            }

            var itemIdSet = availableItemIds.ToHashSet();

            // Single bulk query for all played episodes replaces one DB query per series.
            var seriesLastPlayedMap = BuildSeriesLastPlayedMap(user);

            foreach (var itemId in itemIdSet)
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    continue;
                }

                var userData = _userDataManager.GetUserData(user, item);

                if (userData == null)
                {
                    continue;
                }

                // For series, userData.Played is unreliable — it only becomes true when ALL
                // episodes are watched. Use the most recent watched episode date instead.
                DateTime lastPlayedDate;
                if (item is Series series)
                {
                    if (!seriesLastPlayedMap.TryGetValue(series.Id, out lastPlayedDate))
                    {
                        continue;
                    }
                }
                else if (!userData.Played)
                {
                    // For movies, Played = true means the movie was completed.
                    continue;
                }
                else
                {
                    lastPlayedDate = userData.LastPlayedDate ?? DateTime.UtcNow;
                }

                var record = new WatchRecord(itemId, userId, lastPlayedDate)
                {
                    IsFavorite = userData.IsFavorite,
                    CommunityRating = item.CommunityRating,
                    CriticRating = item.CriticRating
                };

                records.Add(record);
            }

            return records;
        }

        /// <summary>
        /// Builds a map of series ID to the most recent watched episode date for a user.
        /// Uses a single bulk query ordered newest-first; the first episode seen per series
        /// is the most recent, so subsequent episodes for that series are skipped.
        /// </summary>
        private Dictionary<Guid, DateTime> BuildSeriesLastPlayedMap(Jellyfin.Database.Implementations.Entities.User user)
        {
            var playedEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsPlayed = true,
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, Jellyfin.Database.Implementations.Enums.SortOrder.Descending) }
            });

            var map = new Dictionary<Guid, DateTime>();
            foreach (var episode in playedEpisodes)
            {
                if (episode is not Episode ep)
                {
                    continue;
                }

                var seriesId = ep.SeriesId;
                if (seriesId == Guid.Empty || map.ContainsKey(seriesId))
                {
                    continue;
                }

                var epData = _userDataManager.GetUserData(user, episode);
                map[seriesId] = epData?.LastPlayedDate ?? DateTime.UtcNow;
            }

            return map;
        }

        /// <summary>
        /// Computes the user's taste vector as a weighted sum of watched item embeddings.
        /// </summary>
        /// <param name="watchRecords">User's watch records.</param>
        /// <param name="embeddings">Item embeddings.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Normalized taste vector.</returns>
        private (float[] TasteVector, List<(Guid ItemId, float Weight, bool IsFavorite, double DaysSince)> Contributions) ComputeTasteVector(
            List<WatchRecord> watchRecords,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            PluginConfiguration config)
        {
            // Get dimension from first embedding
            var firstEmbedding = embeddings.Values.FirstOrDefault();
            if (firstEmbedding == null)
            {
                throw new InvalidOperationException("No embeddings available");
            }

            var dimension = firstEmbedding.Dimensions;
            var weightedSum = new float[dimension];
            float totalWeight = 0;
            var contributions = new List<(Guid ItemId, float Weight, bool IsFavorite, double DaysSince)>();

            var now = DateTime.UtcNow;

            // Accumulate weighted embeddings for each watched item
            foreach (var record in watchRecords)
            {
                if (!embeddings.TryGetValue(record.ItemId, out var embedding))
                {
                    continue; // Skip items without embeddings
                }

                // Compute combined weight: recency decay × favorite boost × rewatch boost
                var daysSince = (now - record.LastPlayedDate).TotalDays;
                var weight = WeightCalculator.ComputeCombinedWeight(
                    daysSince,
                    config.RecencyDecayHalfLifeDays,
                    record.IsFavorite,
                    (float)config.FavoriteBoost,
                    (float)config.RecentWatchBoost);

                contributions.Add((record.ItemId, weight, record.IsFavorite, daysSince));

                // Accumulate weighted vectors
                for (int i = 0; i < dimension; i++)
                {
                    weightedSum[i] += embedding.Vector[i] * weight;
                }

                totalWeight += weight;
            }

            // Normalize by total weight to get weighted average, then normalize to unit length
            if (totalWeight > 0)
            {
                for (int i = 0; i < dimension; i++)
                {
                    weightedSum[i] /= totalWeight;
                }
            }

            return (VectorMath.Normalize(weightedSum), contributions);
        }

        /// <summary>
        /// Computes rating statistics from watched items.
        /// Uses ratings cached in WatchRecord to avoid duplicate library lookups.
        /// </summary>
        /// <param name="watchRecords">User's watch records (with ratings already populated).</param>
        /// <returns>Rating statistics with averages and standard deviations.</returns>
        private (float? AvgCommunityRating, float? AvgCriticRating, float CommunityStdDev, float CriticStdDev) ComputeRatingStatistics(
            List<WatchRecord> watchRecords)
        {
            // Extract ratings from watch records (already populated during GetWatchRecords)
            var communityRatings = watchRecords
                .Where(r => r.CommunityRating.HasValue)
                .Select(r => r.CommunityRating!.Value)
                .ToList();

            var criticRatings = watchRecords
                .Where(r => r.CriticRating.HasValue)
                .Select(r => r.CriticRating!.Value)
                .ToList();

            // Compute community rating statistics
            float? avgCommunity = null;
            float communityStdDev = 0f;
            if (communityRatings.Any())
            {
                avgCommunity = communityRatings.Average();
                if (communityRatings.Count > 1)
                {
                    var variance = communityRatings.Average(r => Math.Pow(r - avgCommunity.Value, 2));
                    communityStdDev = (float)Math.Sqrt(variance);
                }
            }

            // Compute critic rating statistics
            float? avgCritic = null;
            float criticStdDev = 0f;
            if (criticRatings.Any())
            {
                avgCritic = criticRatings.Average();
                if (criticRatings.Count > 1)
                {
                    var variance = criticRatings.Average(r => Math.Pow(r - avgCritic.Value, 2));
                    criticStdDev = (float)Math.Sqrt(variance);
                }
            }

            return (avgCommunity, avgCritic, communityStdDev, criticStdDev);
        }
    }
}
