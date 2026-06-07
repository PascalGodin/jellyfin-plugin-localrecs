using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Service for refreshing recommendations for users.
    /// Used by the scheduled task to generate recommendations for all users.
    /// No caching - computes fresh recommendations on every run.
    /// </summary>
    public class RecommendationRefreshService
    {
        private readonly ILogger<RecommendationRefreshService> _logger;
        private readonly LibraryAnalysisService _libraryAnalysisService;
        private readonly VocabularyBuilder _vocabularyBuilder;
        private readonly EmbeddingService _embeddingService;
        private readonly UserProfileService _userProfileService;
        private readonly RecommendationEngine _recommendationEngine;
        private readonly DiagnosticLogService _diagnosticLogService;
        private readonly LeavingSoonService _leavingSoonService;
        private readonly IUserManager _userManager;
        private readonly VirtualLibraryManager _virtualLibraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationRefreshService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryAnalysisService">Library analysis service.</param>
        /// <param name="vocabularyBuilder">Vocabulary builder.</param>
        /// <param name="embeddingService">Embedding service.</param>
        /// <param name="userProfileService">User profile service.</param>
        /// <param name="recommendationEngine">Recommendation engine.</param>
        /// <param name="diagnosticLogService">Diagnostic log service.</param>
        /// <param name="leavingSoonService">Leaving Soon scoring service.</param>
        /// <param name="userManager">User manager for resolving usernames.</param>
        /// <param name="virtualLibraryManager">Virtual library manager for Leaving Soon paths.</param>
        public RecommendationRefreshService(
            ILogger<RecommendationRefreshService> logger,
            LibraryAnalysisService libraryAnalysisService,
            VocabularyBuilder vocabularyBuilder,
            EmbeddingService embeddingService,
            UserProfileService userProfileService,
            RecommendationEngine recommendationEngine,
            DiagnosticLogService diagnosticLogService,
            LeavingSoonService leavingSoonService,
            IUserManager userManager,
            VirtualLibraryManager virtualLibraryManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryAnalysisService = libraryAnalysisService ?? throw new ArgumentNullException(nameof(libraryAnalysisService));
            _vocabularyBuilder = vocabularyBuilder ?? throw new ArgumentNullException(nameof(vocabularyBuilder));
            _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _recommendationEngine = recommendationEngine ?? throw new ArgumentNullException(nameof(recommendationEngine));
            _diagnosticLogService = diagnosticLogService ?? throw new ArgumentNullException(nameof(diagnosticLogService));
            _leavingSoonService = leavingSoonService ?? throw new ArgumentNullException(nameof(leavingSoonService));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
        }

        /// <summary>
        /// Computes fresh embeddings for the library.
        /// Always recomputes to ensure recommendations reflect current watch history.
        /// </summary>
        /// <returns>Tuple of embeddings, metadata, vocabulary, and per-phase durations.</returns>
        public (IReadOnlyDictionary<Guid, ItemEmbedding> Embeddings, IReadOnlyDictionary<Guid, MediaItemMetadata> Metadata, FeatureVocabulary Vocabulary, TimeSpan LibraryScan, TimeSpan VocabularyBuild, TimeSpan EmbeddingCompute) ComputeEmbeddings()
        {
            var t0 = DateTime.UtcNow;
            var library = _libraryAnalysisService.GetAllMediaItems();
            var metadata = library.ToDictionary(m => m.Id);
            var libraryScan = DateTime.UtcNow - t0;

            _logger.LogDebug("Computing fresh embeddings for {Count} items", library.Count);

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            var t1 = DateTime.UtcNow;
            var vocabulary = _vocabularyBuilder.BuildVocabulary(
                library,
                config.MaxVocabularyActors,
                config.MaxVocabularyDirectors,
                config.MaxVocabularyTags);
            var vocabularyBuild = DateTime.UtcNow - t1;

            var t2 = DateTime.UtcNow;
            var embeddings = _embeddingService.ComputeEmbeddings(library, vocabulary);
            var embeddingCompute = DateTime.UtcNow - t2;

            var embeddingsDict = embeddings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return (embeddingsDict, metadata, vocabulary, libraryScan, vocabularyBuild, embeddingCompute);
        }

        /// <summary>
        /// Generates recommendations for a single user.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="embeddings">Pre-computed item embeddings.</param>
        /// <param name="metadata">Item metadata dictionary.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Movie and TV recommendations, warm-start flag, watched item count, user profile, exclusion counts, score distributions, and watch records.</returns>
        public (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv, bool WarmStart, int WatchedItemCount, UserProfile? Profile, ExclusionCounts MovieExclusions, ExclusionCounts TvExclusions, ScoreDistribution MovieScoreDist, ScoreDistribution TvScoreDist, IReadOnlyList<WatchRecord> WatchRecords) GenerateRecommendationsForUser(
            Guid userId,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            PluginConfiguration config)
        {
            _logger.LogDebug("Generating recommendations for user {UserId}", userId);

            try
            {
                var (profile, watchRecords) = _userProfileService.BuildUserProfile(userId, embeddings, config);
                var warmStart = profile != null && profile.WatchedItemCount >= config.MinWatchedItemsForPersonalization;
                var watchedCount = profile?.WatchedItemCount ?? 0;

                var movieRecs = _recommendationEngine.GenerateRecommendations(
                    userId,
                    profile,
                    embeddings,
                    metadata,
                    config,
                    MediaType.Movie,
                    config.MovieRecommendationCount);
                var movieExclusions = _recommendationEngine.LastExclusionCounts;
                var movieScoreDist = _recommendationEngine.LastScoreDistribution;

                var tvRecs = _recommendationEngine.GenerateRecommendations(
                    userId,
                    profile,
                    embeddings,
                    metadata,
                    config,
                    MediaType.Series,
                    config.TvRecommendationCount);
                var tvExclusions = _recommendationEngine.LastExclusionCounts;
                var tvScoreDist = _recommendationEngine.LastScoreDistribution;

                _logger.LogDebug(
                    "Generated recommendations for user {UserId}: {MovieCount} movies, {TvCount} TV",
                    userId,
                    movieRecs.Count,
                    tvRecs.Count);

                return (movieRecs, tvRecs, warmStart, watchedCount, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist, watchRecords);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate recommendations for user {UserId}", userId);
                return (new List<ScoredRecommendation>(), new List<ScoredRecommendation>(), false, 0, null, new ExclusionCounts(), new ExclusionCounts(), new ScoreDistribution(), new ScoreDistribution(), Array.Empty<WatchRecord>());
            }
        }

        /// <summary>
        /// Generates recommendations for multiple users efficiently.
        /// Computes embeddings once, reuses them for all users, then writes a diagnostic log.
        /// </summary>
        /// <param name="userIds">List of user IDs to process.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="startTime">Task start time (for duration in the log).</param>
        /// <returns>User recommendations, Leaving Soon state (null when disabled or no eligible profiles), and all scored media items.</returns>
        public Task<(Dictionary<Guid, (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv)> UserRecs, LeavingSoonState? LeavingSoonState, IReadOnlyList<MediaItemMetadata> AllItems)> GenerateRecommendationsForMultipleUsersAsync(
            IReadOnlyList<Guid> userIds,
            PluginConfiguration config,
            DateTime startTime)
        {
            var results = new Dictionary<Guid, (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv)>();

            if (userIds.Count == 0)
            {
                return Task.FromResult((results, (LeavingSoonState?)null, (IReadOnlyList<MediaItemMetadata>)Array.Empty<MediaItemMetadata>()));
            }

            _logger.LogInformation("Generating recommendations for {Count} users", userIds.Count);

            var (embeddings, metadata, vocabulary, libraryScan, vocabularyBuild, embeddingCompute) = ComputeEmbeddings();

            // Per-user data collected for the diagnostic log
            var userLogEntries = new List<(string Username, bool WarmStart, int WatchedCount,
                List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv,
                UserProfile? Profile, ExclusionCounts MovieExclusions, ExclusionCounts TvExclusions,
                ScoreDistribution MovieScoreDist, ScoreDistribution TvScoreDist,
                TimeSpan UserDuration)>();

            var watchStatus = new Dictionary<Guid, (DateTime? LatestWatchDate, bool IsAnyFavorite)>();

            foreach (var userId in userIds)
            {
                var userStart = DateTime.UtcNow;
                var (movies, tv, warmStart, watchedCount, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist, watchRecords) =
                    GenerateRecommendationsForUser(userId, embeddings, metadata, config);
                var userDuration = DateTime.UtcNow - userStart;

                results[userId] = (movies, tv);

                foreach (var record in watchRecords)
                {
                    if (watchStatus.TryGetValue(record.ItemId, out var existing))
                    {
                        var newLatest = (!existing.LatestWatchDate.HasValue || (record.LastPlayedDate > existing.LatestWatchDate.Value))
                            ? (DateTime?)record.LastPlayedDate
                            : existing.LatestWatchDate;
                        watchStatus[record.ItemId] = (newLatest, existing.IsAnyFavorite || record.IsFavorite);
                    }
                    else
                    {
                        watchStatus[record.ItemId] = (record.LastPlayedDate, record.IsFavorite);
                    }
                }

                // Played items are captured above. Unplayed items that are favorited or in-progress
                // are absent from watchRecords. Favorites are gated; in-progress items are not gated
                // but their LastPlayedDate feeds the effective-age calculation so the min-age threshold
                // naturally protects recently-started titles without permanently shielding abandoned ones.
                var protectedStatuses = _userProfileService.GetProtectedItemStatuses(userId, embeddings.Keys);
                foreach (var (itemId, isFavorite, lastPlayedDate) in protectedStatuses)
                {
                    if (watchStatus.TryGetValue(itemId, out var ws))
                    {
                        var newLatest = lastPlayedDate.HasValue && (!ws.LatestWatchDate.HasValue || lastPlayedDate.Value > ws.LatestWatchDate.Value)
                            ? lastPlayedDate
                            : ws.LatestWatchDate;
                        watchStatus[itemId] = (newLatest, ws.IsAnyFavorite || isFavorite);
                    }
                    else
                    {
                        watchStatus[itemId] = (lastPlayedDate, isFavorite);
                    }
                }

                var username = _userManager.GetUserById(userId)?.Username ?? userId.ToString();
                userLogEntries.Add((username, warmStart, watchedCount, movies, tv, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist, userDuration));
            }

            _logger.LogInformation("Successfully generated recommendations for {Count}/{Total} users", results.Count, userIds.Count);

            // Virtual Leaving Soon libraries may contain items that users have favorited directly in those
            // libraries. Series items are folders (not symlinks), so PlayStatusSyncService cannot resolve them
            // and the sync to the source item is silently skipped. Scan the virtual libraries here to catch
            // any favorite flags that were not synced before the Leaving Soon gate runs.
            if (config.LeavingSoonEnabled)
            {
                var virtualProtected = _userProfileService.GetVirtualLeavingSoonProtectedStatuses(
                    userIds,
                    new[]
                    {
                        _virtualLibraryManager.LeavingSoonMoviesPath,
                        _virtualLibraryManager.LeavingSoonTvPath,
                        _virtualLibraryManager.RemovalCandidatesMoviesPath,
                        _virtualLibraryManager.RemovalCandidatesTvPath,
                    });
                foreach (var (actualItemId, isFavorite) in virtualProtected)
                {
                    if (watchStatus.TryGetValue(actualItemId, out var ws))
                    {
                        watchStatus[actualItemId] = (ws.LatestWatchDate, ws.IsAnyFavorite || isFavorite);
                    }
                    else
                    {
                        watchStatus[actualItemId] = (null, isFavorite);
                    }
                }
            }

            // Run Leaving Soon scoring using the profiles already computed above
            LeavingSoonState? leavingSoonState = null;
            LeavingSoonDiagnostics? leavingSoonDiagnostics = null;
            var allItems = (IReadOnlyList<MediaItemMetadata>)metadata.Values.ToList();
            if (config.LeavingSoonEnabled)
            {
                var eligibleProfiles = userLogEntries
                    .Where(e => e.WarmStart && e.Profile != null)
                    .Select(e => e.Profile!)
                    .ToList();

                var lsResult = _leavingSoonService.Refresh(
                    allItems,
                    embeddings,
                    eligibleProfiles,
                    watchStatus,
                    config,
                    System.Threading.CancellationToken.None);
                leavingSoonState = lsResult.State;
                leavingSoonDiagnostics = lsResult.Diagnostics;
            }

            if (config.EnableDiagnosticLog)
            {
                WriteDiagnosticLog(startTime, metadata, vocabulary, embeddings, userLogEntries, leavingSoonState, leavingSoonDiagnostics, config, libraryScan, vocabularyBuild, embeddingCompute);
            }

            return Task.FromResult((results, leavingSoonState, allItems));
        }

        private static void AppendExclusionItems(StringBuilder sb, string label, IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            sb.AppendLine($"    {label}:");
            foreach (var name in items)
            {
                sb.AppendLine($"      - {name}");
            }
        }

        private static string FormatExclusionLine(string label, int watched, int inProgress, int inaccessible, int noMetadata, int notFound, int final)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (watched > 0)
            {
                parts.Add($"{watched} watched");
            }

            if (inProgress > 0)
            {
                parts.Add($"{inProgress} in-progress");
            }

            if (inaccessible > 0)
            {
                parts.Add($"{inaccessible} inaccessible");
            }

            if (noMetadata > 0)
            {
                parts.Add($"{noMetadata} no-metadata");
            }

            if (notFound > 0)
            {
                parts.Add($"{notFound} not-found");
            }

            var summary = parts.Count > 0 ? string.Join(", ", parts) : "none";
            return $"  Exclusions ({label}): {summary} → {final} candidates";
        }

        private static void AppendRecommendationList(
            StringBuilder sb,
            string label,
            List<ScoredRecommendation> recs,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            bool warmStart,
            UserProfile? profile = null,
            IReadOnlyDictionary<Guid, ItemEmbedding>? embeddings = null,
            FeatureVocabulary? vocabulary = null)
        {
            var suffix = warmStart ? string.Empty : ", by rating";
            sb.AppendLine($"  {label} ({recs.Count}{suffix}):");

            for (var i = 0; i < recs.Count; i++)
            {
                var rec = recs[i];
                metadata.TryGetValue(rec.ItemId, out var meta);
                var name = meta?.Name ?? rec.ItemId.ToString();
                var year = meta?.ReleaseYear > 0 ? $" ({meta.ReleaseYear})" : string.Empty;
                sb.AppendLine($"    {i + 1,3}.  {rec.Score:F3}  {name}{year}");

                if (rec.CosineSimilarity.HasValue)
                {
                    var detail = $"           content: {rec.CosineSimilarity.Value * 100:F0}%";

                    if (rec.RatingProximity.HasValue)
                    {
                        var parts = new System.Collections.Generic.List<string>();

                        if (rec.ItemCommunityRating.HasValue)
                        {
                            var s = $"community: {rec.ItemCommunityRating.Value:F1}/10";
                            if (profile?.AverageCommunityRating.HasValue == true)
                            {
                                var delta = rec.ItemCommunityRating.Value - profile.AverageCommunityRating.Value;
                                var deltaStr = delta >= 0 ? $"+{delta:F1}" : $"{delta:F1}";
                                s += $" (avg {profile.AverageCommunityRating.Value:F1}, Δ{deltaStr})";
                            }

                            parts.Add(s);
                        }

                        if (rec.ItemCriticRating.HasValue)
                        {
                            var s = $"critic: {rec.ItemCriticRating.Value:F0}/100";
                            if (profile?.AverageCriticRating.HasValue == true)
                            {
                                var delta = rec.ItemCriticRating.Value - profile.AverageCriticRating.Value;
                                var deltaStr = delta >= 0 ? $"+{delta:F0}" : $"{delta:F0}";
                                s += $" (avg {profile.AverageCriticRating.Value:F0}, Δ{deltaStr})";
                            }

                            parts.Add(s);
                        }

                        if (parts.Count > 0)
                        {
                            detail += $"  {string.Join("  ", parts)}";
                        }
                    }

                    sb.AppendLine(detail);

                    if (profile != null && embeddings != null && vocabulary != null
                        && embeddings.TryGetValue(rec.ItemId, out var itemEmb))
                    {
                        var overlap = GetFeatureOverlap(profile.TasteVector, itemEmb.Vector, vocabulary);
                        if (overlap.Count > 0)
                        {
                            sb.AppendLine($"           why: {string.Join(", ", overlap.Select(f => f.Label))}");
                        }
                    }
                }
                else if (!warmStart && (rec.ItemCommunityRating.HasValue || rec.ItemCriticRating.HasValue))
                {
                    var parts = new System.Collections.Generic.List<string>();
                    if (rec.ItemCommunityRating.HasValue)
                    {
                        parts.Add($"community: {rec.ItemCommunityRating.Value:F1}/10");
                    }

                    if (rec.ItemCriticRating.HasValue)
                    {
                        parts.Add($"critic: {rec.ItemCriticRating.Value:F0}/100");
                    }

                    sb.AppendLine($"           {string.Join("  ", parts)}");
                }
            }

            sb.AppendLine();
        }

        private static List<(string Label, float Weight)> GetFeatureOverlap(
            float[] tasteVector,
            float[] itemVector,
            FeatureVocabulary vocabulary,
            int topN = 5)
        {
            var features = new List<(string Label, float Weight)>();
            var offset = 0;

            void AddSection(string type, IReadOnlyDictionary<string, float> idf)
            {
                foreach (var (name, _) in idf)
                {
                    if (offset < tasteVector.Length && offset < itemVector.Length)
                    {
                        features.Add(($"{type}: {name}", tasteVector[offset] * itemVector[offset]));
                    }

                    offset++;
                }
            }

            AddSection("Genre", vocabulary.GenreIdf);
            AddSection("Actor", vocabulary.ActorIdf);
            AddSection("Director", vocabulary.DirectorIdf);
            AddSection("Tag", vocabulary.TagIdf);
            AddSection("Decade", vocabulary.DecadeIdf);

            return features.Where(f => f.Weight > 0f).OrderByDescending(f => f.Weight).Take(topN).ToList();
        }

        private static List<(string Label, float Weight)> GetTopTasteFeatures(
            float[] tasteVector,
            FeatureVocabulary vocabulary,
            int topN = 10)
        {
            var features = new List<(string Label, float Weight)>();
            var offset = 0;

            void AddSection(string type, IReadOnlyDictionary<string, float> idf)
            {
                foreach (var (name, _) in idf)
                {
                    if (offset < tasteVector.Length)
                    {
                        features.Add(($"{type}: {name}", tasteVector[offset]));
                    }

                    offset++;
                }
            }

            AddSection("Genre", vocabulary.GenreIdf);
            AddSection("Actor", vocabulary.ActorIdf);
            AddSection("Director", vocabulary.DirectorIdf);
            AddSection("Tag", vocabulary.TagIdf);
            AddSection("Decade", vocabulary.DecadeIdf);

            return features
                .Where(f => f.Weight > 0f)
                .OrderByDescending(f => f.Weight)
                .Take(topN)
                .ToList();
        }

        private void WriteDiagnosticLog(
            DateTime startTime,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            FeatureVocabulary vocabulary,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            List<(string Username, bool WarmStart, int WatchedCount,
                List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv,
                UserProfile? Profile, ExclusionCounts MovieExclusions, ExclusionCounts TvExclusions,
                ScoreDistribution MovieScoreDist, ScoreDistribution TvScoreDist,
                TimeSpan UserDuration)> userEntries,
            LeavingSoonState? leavingSoonState,
            LeavingSoonDiagnostics? leavingSoonDiagnostics,
            PluginConfiguration config,
            TimeSpan libraryScan,
            TimeSpan vocabularyBuild,
            TimeSpan embeddingCompute)
        {
            var totalDuration = DateTime.UtcNow - startTime;
            var movieCount = metadata.Values.Count(m => m.Type == MediaType.Movie);
            var seriesCount = metadata.Values.Count(m => m.Type == MediaType.Series);
            var embeddingDim = embeddings.Values.FirstOrDefault()?.Vector.Length ?? 0;
            var perUserTotal = userEntries.Aggregate(TimeSpan.Zero, (acc, e) => acc + e.UserDuration);

            var sb = new StringBuilder();
            var line = new string('=', 80);
            var dash = new string('─', 80);

            sb.AppendLine(line);
            sb.AppendLine("LocalRecs Diagnostic Log");
            sb.AppendLine($"Run timestamp : {startTime:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Duration      : {totalDuration.TotalSeconds:F2} s");
            sb.AppendLine(line);
            sb.AppendLine();

            // CONFIG
            var proximityLabel = config.EnableRatingProximity
                ? $"on ({config.RatingProximityWeight:P0} blend)"
                : "off";
            var actorsLabel = config.MaxVocabularyActors > 0 ? $"{config.MaxVocabularyActors} (limited)" : "unlimited";
            var directorsLabel = config.MaxVocabularyDirectors > 0 ? $"{config.MaxVocabularyDirectors} (limited)" : "unlimited";
            var tagsLabel = config.MaxVocabularyTags > 0 ? $"{config.MaxVocabularyTags} (limited)" : "unlimited";
            sb.AppendLine("CONFIG");
            sb.AppendLine("  Recommendations:");
            sb.AppendLine($"    Movies             : {config.MovieRecommendationCount}");
            sb.AppendLine($"    TV shows           : {config.TvRecommendationCount}");
            sb.AppendLine($"    Min watched        : {config.MinWatchedItemsForPersonalization}");
            sb.AppendLine($"    Favorite boost     : {config.FavoriteBoost:F1}×");
            sb.AppendLine($"    Recency half-life  : {config.RecencyDecayHalfLifeDays:F0} d");
            sb.AppendLine($"    Recent watch emph. : {config.RecentWatchBoost:F1}");
            sb.AppendLine($"    Rating proximity   : {proximityLabel}");
            sb.AppendLine("  Vocabulary limits:");
            sb.AppendLine($"    Actors             : {actorsLabel}");
            sb.AppendLine($"    Directors          : {directorsLabel}");
            sb.AppendLine($"    Tags               : {tagsLabel}");
            sb.AppendLine("  Leaving Soon:");
            sb.AppendLine($"    Enabled            : {(config.LeavingSoonEnabled ? "yes" : "no")}");
            sb.AppendLine($"    Min age            : {config.LeavingSoonMinAgeDays} d");
            sb.AppendLine($"    Dwell period       : {config.LeavingSoonDwellDays} d");
            sb.AppendLine($"    Movie target       : {config.LeavingSoonMovieCount}");
            sb.AppendLine($"    TV target          : {config.LeavingSoonTvCount}");
            sb.AppendLine();

            // LIBRARY
            sb.AppendLine("LIBRARY");
            sb.AppendLine($"  Movies  : {movieCount}");
            sb.AppendLine($"  Series  : {seriesCount}");
            sb.AppendLine($"  Total   : {metadata.Count}");
            sb.AppendLine();

            // VOCABULARY
            sb.AppendLine("VOCABULARY");
            sb.AppendLine($"  Genres     : {vocabulary.Genres.Count,4}");
            sb.AppendLine($"  Actors     : {vocabulary.Actors.Count,4}");
            sb.AppendLine($"  Directors  : {vocabulary.Directors.Count,4}");
            sb.AppendLine($"  Tags       : {vocabulary.Tags.Count,4}");
            sb.AppendLine($"  Decades    : {vocabulary.Decades.Count,4}");
            sb.AppendLine($"  Dimensions : {embeddingDim,4}  (total)");
            sb.AppendLine();

            // COVERAGE
            var total = metadata.Count;
            var pct = total > 0 ? 100.0 / total : 0;
            sb.AppendLine("COVERAGE");
            sb.AppendLine($"  Has genres         : {metadata.Values.Count(m => m.Genres.Count > 0),4}  ({metadata.Values.Count(m => m.Genres.Count > 0) * pct:F0}%)");
            sb.AppendLine($"  Has actors         : {metadata.Values.Count(m => m.Actors.Count > 0),4}  ({metadata.Values.Count(m => m.Actors.Count > 0) * pct:F0}%)");
            sb.AppendLine($"  Has directors      : {metadata.Values.Count(m => m.Directors.Count > 0),4}  ({metadata.Values.Count(m => m.Directors.Count > 0) * pct:F0}%)");
            sb.AppendLine($"  Has tags           : {metadata.Values.Count(m => m.Tags.Count > 0),4}  ({metadata.Values.Count(m => m.Tags.Count > 0) * pct:F0}%)");
            sb.AppendLine($"  Community rating   : {metadata.Values.Count(m => m.CommunityRating.HasValue),4}  ({metadata.Values.Count(m => m.CommunityRating.HasValue) * pct:F0}%)");
            sb.AppendLine($"  Critic rating      : {metadata.Values.Count(m => m.CriticRating.HasValue),4}  ({metadata.Values.Count(m => m.CriticRating.HasValue) * pct:F0}%)");
            sb.AppendLine();

            // TIMING
            sb.AppendLine("TIMING");
            sb.AppendLine($"  Library scan  : {libraryScan.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Vocabulary    : {vocabularyBuild.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Embeddings    : {embeddingCompute.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Per user      : {perUserTotal.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Total         : {totalDuration.TotalSeconds,6:F2} s");
            sb.AppendLine();

            // Cold-start users share identical rating-based recommendations — group into one block.
            var coldStartEntries = userEntries.Where(e => !e.WarmStart).ToList();
            if (coldStartEntries.Count > 0)
            {
                sb.AppendLine(dash);
                sb.AppendLine($"COLD-START USERS ({coldStartEntries.Count}) — rating-based, threshold: {config.MinWatchedItemsForPersonalization} items watched");
                sb.AppendLine();
                foreach (var ce in coldStartEntries)
                {
                    var itemWord = ce.WatchedCount == 1 ? "item" : "items";
                    sb.AppendLine($"  {ce.Username,-20} {ce.WatchedCount} {itemWord} watched  ({ce.UserDuration.TotalSeconds:F2} s)");
                }

                sb.AppendLine();
                var firstCold = coldStartEntries[0];
                AppendRecommendationList(sb, "Movies", firstCold.Movies, metadata, false);
                AppendRecommendationList(sb, "TV Shows", firstCold.Tv, metadata, false);
            }

            // Personalized users: individual sections.
            foreach (var entry in userEntries.Where(e => e.WarmStart))
            {
                sb.AppendLine(dash);
                sb.AppendLine($"USER: {entry.Username}  ({entry.UserDuration.TotalSeconds:F2} s)");
                sb.AppendLine($"  Profile : personalized ({entry.WatchedCount} items watched)");

                if (entry.Profile != null)
                {
                    var communityStr = entry.Profile.AverageCommunityRating.HasValue
                        ? $"community {entry.Profile.AverageCommunityRating.Value:F1}/10 (±{entry.Profile.CommunityRatingStdDev:F1})"
                        : "community n/a";
                    var criticStr = entry.Profile.AverageCriticRating.HasValue
                        ? $"critic {entry.Profile.AverageCriticRating.Value:F0}/100 (±{entry.Profile.CriticRatingStdDev:F0})"
                        : "critic n/a";
                    sb.AppendLine($"  Ratings : {communityStr}  {criticStr}");

                    if (entry.Profile.TopWatchContributions.Count > 0)
                    {
                        var maxContribWeight = entry.Profile.TopWatchContributions[0].Weight;
                        sb.AppendLine($"  Top watched (by taste weight):");
                        for (var i = 0; i < entry.Profile.TopWatchContributions.Count; i++)
                        {
                            var c = entry.Profile.TopWatchContributions[i];
                            metadata.TryGetValue(c.ItemId, out var watchMeta);
                            var watchName = watchMeta?.Name ?? c.ItemId.ToString();
                            var watchYear = watchMeta?.ReleaseYear > 0 ? $" ({watchMeta.ReleaseYear})" : string.Empty;
                            var flags = new System.Collections.Generic.List<string>();
                            if (c.IsFavorite)
                            {
                                flags.Add("favorite");
                            }

                            var age = c.DaysSince < 365 ? $"{c.DaysSince:F0}d ago" : $"{c.DaysSince / 365:F1}y ago";
                            flags.Add(age);
                            var contribPct = maxContribWeight > 0 ? c.Weight / maxContribWeight * 100 : 0;
                            sb.AppendLine($"    {i + 1,3}.  {contribPct,3:F0}%  {watchName}{watchYear}  [{string.Join("  ", flags)}]");
                            if (embeddings.TryGetValue(c.ItemId, out var watchEmb))
                            {
                                var itemFeatures = GetTopTasteFeatures(watchEmb.Vector, vocabulary, 3);
                                if (itemFeatures.Count > 0)
                                {
                                    sb.AppendLine($"                {string.Join(", ", itemFeatures.Select(f => f.Label))}");
                                }
                            }
                        }
                    }
                }

                sb.AppendLine();
                sb.AppendLine(FormatExclusionLine("movies", entry.MovieExclusions.Watched, entry.MovieExclusions.InProgress, entry.MovieExclusions.Inaccessible, entry.MovieExclusions.NoMetadata, entry.MovieExclusions.NotFound, entry.MovieExclusions.Final));
                AppendExclusionItems(sb, "no-metadata", entry.MovieExclusions.NoMetadataItems);
                AppendExclusionItems(sb, "not-found", entry.MovieExclusions.NotFoundItems);
                sb.AppendLine(FormatExclusionLine("TV", entry.TvExclusions.SeriesWatched, entry.TvExclusions.InProgress, entry.TvExclusions.Inaccessible, entry.TvExclusions.NoMetadata, entry.TvExclusions.NotFound, entry.TvExclusions.Final));
                AppendExclusionItems(sb, "no-metadata", entry.TvExclusions.NoMetadataItems);
                AppendExclusionItems(sb, "not-found", entry.TvExclusions.NotFoundItems);

                if (entry.Profile != null)
                {
                    var topFeatures = GetTopTasteFeatures(entry.Profile.TasteVector, vocabulary);
                    if (topFeatures.Count > 0)
                    {
                        var maxTasteWeight = topFeatures[0].Weight;
                        sb.AppendLine();
                        sb.AppendLine($"  Top taste signals:");
                        for (var i = 0; i < topFeatures.Count; i++)
                        {
                            var tastePct = maxTasteWeight > 0 ? topFeatures[i].Weight / maxTasteWeight * 100 : 0;
                            sb.AppendLine($"    {i + 1,3}.  {tastePct,3:F0}%  {topFeatures[i].Label}");
                        }
                    }
                }

                sb.AppendLine();
                AppendRecommendationList(sb, "Movies", entry.Movies, metadata, true, entry.Profile, embeddings, vocabulary);
                AppendRecommendationList(sb, "TV Shows", entry.Tv, metadata, true, entry.Profile, embeddings, vocabulary);
            }

            // LEAVING SOON
            if (leavingSoonState != null)
            {
                sb.AppendLine(dash);
                sb.AppendLine("LEAVING SOON");
                sb.AppendLine();

                if (leavingSoonDiagnostics != null)
                {
                    sb.AppendLine($"  Discovery ({leavingSoonDiagnostics.EligibleProfileCount} personalized profile{(leavingSoonDiagnostics.EligibleProfileCount == 1 ? string.Empty : "s")}):");
                    sb.AppendLine($"    Scored           : {leavingSoonDiagnostics.ScoredMovies} movies, {leavingSoonDiagnostics.ScoredTv} TV");
                    sb.AppendLine($"    Skipped:");
                    sb.AppendLine($"      Already removal  : {leavingSoonDiagnostics.SkippedAlreadyRemoval}");
                    sb.AppendLine($"      No metadata      : {leavingSoonDiagnostics.SkippedNoMetadata}");
                    sb.AppendLine($"      No embedding     : {leavingSoonDiagnostics.SkippedNoEmbedding}");
                    sb.AppendLine($"      Favorited        : {leavingSoonDiagnostics.SkippedAlwaysSafe}");
                    sb.AppendLine($"      Too young (<{config.LeavingSoonMinAgeDays}d): {leavingSoonDiagnostics.SkippedTooYoung}");
                    sb.AppendLine($"      Not found        : {leavingSoonDiagnostics.SkippedNotFound}");
                    sb.AppendLine($"      Unknown type     : {leavingSoonDiagnostics.SkippedUnknownType}");

                    if (leavingSoonDiagnostics.TooYoungItems.Count > 0)
                    {
                        sb.AppendLine($"    Too young details ({leavingSoonDiagnostics.TooYoungItems.Count}):");
                        var tyResolved = leavingSoonDiagnostics.TooYoungItems
                            .Select(x =>
                            {
                                metadata.TryGetValue(x.ItemId, out var m);
                                return (x.ItemId, x.DaysUntilEligible, Meta: m);
                            })
                            .ToList();
                        var tyMovies = tyResolved.Where(x => x.Meta?.Type != MediaType.Series).ToList();
                        var tyTv = tyResolved.Where(x => x.Meta?.Type == MediaType.Series).ToList();
                        if (tyMovies.Count > 0)
                        {
                            sb.AppendLine($"      Movies ({tyMovies.Count}):");
                            foreach (var item in tyMovies)
                            {
                                var tyName = item.Meta?.Name ?? item.ItemId.ToString();
                                var tyYear = item.Meta?.ReleaseYear > 0 ? $" ({item.Meta.ReleaseYear})" : string.Empty;
                                sb.AppendLine($"        - {tyName}{tyYear}  [{item.DaysUntilEligible:F0}d until eligible]");
                            }
                        }

                        if (tyTv.Count > 0)
                        {
                            sb.AppendLine($"      TV ({tyTv.Count}):");
                            foreach (var item in tyTv)
                            {
                                var tyName = item.Meta?.Name ?? item.ItemId.ToString();
                                var tyYear = item.Meta?.ReleaseYear > 0 ? $" ({item.Meta.ReleaseYear})" : string.Empty;
                                sb.AppendLine($"        - {tyName}{tyYear}  [{item.DaysUntilEligible:F0}d until eligible]");
                            }
                        }
                    }

                    sb.AppendLine();
                }

                var now = DateTime.UtcNow;

                if (leavingSoonState.FlaggedItems.Count == 0)
                {
                    sb.AppendLine("  Flagged (0)");
                }
                else
                {
                    sb.AppendLine($"  Flagged ({leavingSoonState.FlaggedItems.Count}):");
                    var flaggedResolved = leavingSoonState.FlaggedItems
                        .OrderByDescending(kvp => kvp.Value)
                        .Select(kvp =>
                        {
                            var idGuid = Guid.TryParse(kvp.Key, out var g) ? g : Guid.Empty;
                            metadata.TryGetValue(idGuid, out var m);
                            return (Id: kvp.Key, FlaggedAt: kvp.Value, Meta: m);
                        })
                        .ToList();
                    var flaggedMovies = flaggedResolved.Where(x => x.Meta?.Type != MediaType.Series).ToList();
                    var flaggedTv = flaggedResolved.Where(x => x.Meta?.Type == MediaType.Series).ToList();
                    if (flaggedMovies.Count > 0)
                    {
                        sb.AppendLine($"    Movies ({flaggedMovies.Count}):");
                        for (var i = 0; i < flaggedMovies.Count; i++)
                        {
                            var item = flaggedMovies[i];
                            var name = item.Meta?.Name ?? item.Id;
                            var year = item.Meta?.ReleaseYear > 0 ? $" ({item.Meta.ReleaseYear})" : string.Empty;
                            var daysAgo = (now - item.FlaggedAt).TotalDays;
                            sb.AppendLine($"      {i + 1,3}.  {name}{year}  [flagged {daysAgo:F0}d ago]");
                        }
                    }

                    if (flaggedTv.Count > 0)
                    {
                        sb.AppendLine($"    TV ({flaggedTv.Count}):");
                        for (var i = 0; i < flaggedTv.Count; i++)
                        {
                            var item = flaggedTv[i];
                            var name = item.Meta?.Name ?? item.Id;
                            var year = item.Meta?.ReleaseYear > 0 ? $" ({item.Meta.ReleaseYear})" : string.Empty;
                            var daysAgo = (now - item.FlaggedAt).TotalDays;
                            sb.AppendLine($"      {i + 1,3}.  {name}{year}  [flagged {daysAgo:F0}d ago]");
                        }
                    }
                }

                sb.AppendLine();

                if (leavingSoonState.RemovalCandidates.Count == 0)
                {
                    sb.AppendLine("  Removal candidates (0)");
                }
                else
                {
                    sb.AppendLine($"  Removal candidates ({leavingSoonState.RemovalCandidates.Count}):");
                    var removalResolved = leavingSoonState.RemovalCandidates
                        .OrderByDescending(kvp => kvp.Value)
                        .Select(kvp =>
                        {
                            var idGuid = Guid.TryParse(kvp.Key, out var g) ? g : Guid.Empty;
                            metadata.TryGetValue(idGuid, out var m);
                            return (Id: kvp.Key, PromotedAt: kvp.Value, Meta: m);
                        })
                        .ToList();
                    var removalMovies = removalResolved.Where(x => x.Meta?.Type != MediaType.Series).ToList();
                    var removalTv = removalResolved.Where(x => x.Meta?.Type == MediaType.Series).ToList();
                    if (removalMovies.Count > 0)
                    {
                        sb.AppendLine($"    Movies ({removalMovies.Count}):");
                        for (var i = 0; i < removalMovies.Count; i++)
                        {
                            var item = removalMovies[i];
                            var name = item.Meta?.Name ?? item.Id;
                            var year = item.Meta?.ReleaseYear > 0 ? $" ({item.Meta.ReleaseYear})" : string.Empty;
                            var daysInRemoval = (now - item.PromotedAt).TotalDays;
                            sb.AppendLine($"      {i + 1,3}.  {name}{year}  [in removal {daysInRemoval:F0}d]");
                        }
                    }

                    if (removalTv.Count > 0)
                    {
                        sb.AppendLine($"    TV ({removalTv.Count}):");
                        for (var i = 0; i < removalTv.Count; i++)
                        {
                            var item = removalTv[i];
                            var name = item.Meta?.Name ?? item.Id;
                            var year = item.Meta?.ReleaseYear > 0 ? $" ({item.Meta.ReleaseYear})" : string.Empty;
                            var daysInRemoval = (now - item.PromotedAt).TotalDays;
                            sb.AppendLine($"      {i + 1,3}.  {name}{year}  [in removal {daysInRemoval:F0}d]");
                        }
                    }
                }

                sb.AppendLine();
            }

            sb.AppendLine(line);

            _diagnosticLogService.Save(sb.ToString());
        }
    }
}
