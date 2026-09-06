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
    /// Items with the lowest max-user taste score (the same content+rating blend used for
    /// recommendations) are flagged as Leaving Soon. After a configurable dwell period they
    /// are promoted to Removal Candidates.
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
        /// <param name="recommendedItemIds">Item IDs currently appearing in any user's recommendation list this run; these are never flagged.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Updated Leaving Soon state and discovery diagnostics after this run.</returns>
        public (LeavingSoonState State, LeavingSoonDiagnostics Diagnostics) Refresh(
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyList<UserProfile> eligibleProfiles,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus,
            IReadOnlySet<Guid> recommendedItemIds,
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

            if (recommendedItemIds == null)
            {
                throw new ArgumentNullException(nameof(recommendedItemIds));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _logger.LogInformation("Starting Leaving Soon refresh");

            var state = LoadState();

            var now = DateTime.UtcNow;
            var minAge = TimeSpan.FromDays(config.LeavingSoonMinAgeDays);

            // Pass 1: Promote items that have exceeded the dwell period
            Promote(state, watchStatus, config);
            cancellationToken.ThrowIfCancellationRequested();

            var knownItemIds = new HashSet<string>(allItems.Select(a => a.Id.ToString()), StringComparer.OrdinalIgnoreCase);

            // Pass 2: Discover new candidates and reconcile FlaggedItems
            LeavingSoonDiagnostics diagnostics;
            if (eligibleProfiles.Count > 0)
            {
                diagnostics = Discover(state, allItems, embeddings, eligibleProfiles, watchStatus, recommendedItemIds, config, minAge, now);
            }
            else
            {
                _logger.LogDebug("Leaving Soon discovery skipped: no users have enough watch history");
                diagnostics = new LeavingSoonDiagnostics();
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Pass 3: Remove removal candidate entries for items that no longer exist in the library,
            // or that have since been favorited. FlaggedItems are already cleaned up by discovery
            // eviction (Pass 2) — deleted or now-favorited items aren't scored and fall out of the
            // target set, so they get evicted automatically. RemovalCandidates have no such mechanism:
            // once promoted, entries accumulate indefinitely unless explicitly pruned here.
            var removedRemoval = state.RemovalCandidates.Keys
                .Where(id => !knownItemIds.Contains(id) || IsFavorited(id, watchStatus))
                .ToList();
            foreach (var id in removedRemoval)
            {
                state.RemovalCandidates.Remove(id);
            }

            if (removedRemoval.Count > 0)
            {
                _logger.LogInformation(
                    "Leaving Soon cleanup: removed {RemovedRemoval} removal candidates (no longer in library or now favorited)",
                    removedRemoval.Count);
            }

            SaveState(state);

            _logger.LogInformation(
                "Leaving Soon refresh complete: {FlaggedCount} flagged, {RemovalCount} removal candidates",
                state.FlaggedItems.Count,
                state.RemovalCandidates.Count);

            return (state, diagnostics);
        }

        private static bool IsFavorited(
            string itemId,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus)
        {
            return Guid.TryParse(itemId, out var id) && watchStatus.TryGetValue(id, out var ws) && ws.IsAnyFavorite;
        }

        private void Promote(
            LeavingSoonState state,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus,
            PluginConfiguration config)
        {
            var dueForAction = state.FlaggedItems
                .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalDays >= config.LeavingSoonDwellDays)
                .Select(kvp => kvp.Key)
                .ToList();

            var toPromote = new List<string>();
            var toUnflag = new List<string>();

            foreach (var id in dueForAction)
            {
                // Favorited after being flagged but before the dwell period elapsed — unflag
                // instead of promoting, since favoriting makes an item permanently safe.
                (IsFavorited(id, watchStatus) ? toUnflag : toPromote).Add(id);
            }

            foreach (var id in toPromote)
            {
                state.FlaggedItems.Remove(id);
                state.RemovalCandidates[id] = DateTime.UtcNow;
            }

            foreach (var id in toUnflag)
            {
                state.FlaggedItems.Remove(id);
            }

            if (toPromote.Count > 0 || toUnflag.Count > 0)
            {
                _logger.LogDebug(
                    "Leaving Soon promotion: moved {Count} items to Removal Candidates, unflagged {UnflaggedCount} now-favorited items",
                    toPromote.Count,
                    toUnflag.Count);
            }
        }

        private LeavingSoonDiagnostics Discover(
            LeavingSoonState state,
            IReadOnlyList<MediaItemMetadata> allItems,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyList<UserProfile> eligibleProfiles,
            IReadOnlyDictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)> watchStatus,
            IReadOnlySet<Guid> recommendedItemIds,
            PluginConfiguration config,
            TimeSpan minAge,
            DateTime now)
        {
            var diag = new LeavingSoonDiagnostics
            {
                EligibleProfileCount = eligibleProfiles.Count
            };

            var scoredMovies = new List<(string Id, float Score, double DaysUntilEligible)>();
            var scoredTv = new List<(string Id, float Score, double DaysUntilEligible)>();

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

                watchStatus.TryGetValue(meta.Id, out var ws);

                if (ws.IsAnyFavorite)
                {
                    diag.SkippedAlwaysSafe++;
                    continue;
                }

                // An item currently sitting in someone's recommendation list is, by definition, not
                // a removal candidate this run — regardless of how it scores against the household.
                if (recommendedItemIds.Contains(meta.Id))
                {
                    diag.SkippedCurrentlyRecommended++;
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
                double daysUntilEligible = effectiveAge >= minAge ? 0 : (minAge - effectiveAge).TotalDays;

                // Use the same content+rating blend as recommendations, so an item that scores
                // well enough to be recommended to someone can't simultaneously look like the
                // worst taste match to everyone and get flagged for removal.
                var maxSimilarity = 0f;
                foreach (var profile in eligibleProfiles)
                {
                    var score = TasteScoring.Compute(
                        profile.TasteVector,
                        profile.AverageCommunityRating,
                        profile.AverageCriticRating,
                        embedding.Vector,
                        meta.CommunityRating,
                        meta.CriticRating,
                        config.EnableRatingProximity,
                        config.RatingProximityWeight).FinalScore;
                    if (score > maxSimilarity)
                    {
                        maxSimilarity = score;
                    }
                }

                if (meta.Type == MediaType.Movie)
                {
                    diag.ScoredMovies++;
                    scoredMovies.Add((id, maxSimilarity, daysUntilEligible));
                }
                else if (meta.Type == MediaType.Series)
                {
                    diag.ScoredTv++;
                    scoredTv.Add((id, maxSimilarity, daysUntilEligible));
                }
                else
                {
                    diag.SkippedUnknownType++;
                }
            }

            // Sort globally, take the worst X, then age-gate — may produce fewer than X results.
            var topMovies = scoredMovies.OrderBy(x => x.Score).Take(config.LeavingSoonMovieCount).ToList();
            var topTv = scoredTv.OrderBy(x => x.Score).Take(config.LeavingSoonTvCount).ToList();

            diag.SkippedTooYoung = topMovies.Count(x => x.DaysUntilEligible > 0) + topTv.Count(x => x.DaysUntilEligible > 0);

            foreach (var item in topMovies.Concat(topTv).Where(x => x.DaysUntilEligible > 0))
            {
                var itemId = Guid.TryParse(item.Id, out var g) ? g : Guid.Empty;
                diag.TooYoungItems.Add((itemId, item.DaysUntilEligible));
            }

            var targetMovies = topMovies.Where(x => x.DaysUntilEligible == 0).Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetTv = topTv.Where(x => x.DaysUntilEligible == 0).Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var evicted = state.FlaggedItems.Keys
                .Where(id => !targetMovies.Contains(id) && !targetTv.Contains(id))
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
