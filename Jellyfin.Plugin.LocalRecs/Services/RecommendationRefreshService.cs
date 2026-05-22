using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
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
        private readonly IUserManager _userManager;

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
        /// <param name="userManager">User manager for resolving usernames.</param>
        public RecommendationRefreshService(
            ILogger<RecommendationRefreshService> logger,
            LibraryAnalysisService libraryAnalysisService,
            VocabularyBuilder vocabularyBuilder,
            EmbeddingService embeddingService,
            UserProfileService userProfileService,
            RecommendationEngine recommendationEngine,
            DiagnosticLogService diagnosticLogService,
            IUserManager userManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryAnalysisService = libraryAnalysisService ?? throw new ArgumentNullException(nameof(libraryAnalysisService));
            _vocabularyBuilder = vocabularyBuilder ?? throw new ArgumentNullException(nameof(vocabularyBuilder));
            _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _recommendationEngine = recommendationEngine ?? throw new ArgumentNullException(nameof(recommendationEngine));
            _diagnosticLogService = diagnosticLogService ?? throw new ArgumentNullException(nameof(diagnosticLogService));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
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
        /// <returns>Movie and TV recommendations, warm-start flag, watched item count, user profile, and exclusion counts.</returns>
        public (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv, bool WarmStart, int WatchedItemCount, UserProfile? Profile, ExclusionCounts MovieExclusions, ExclusionCounts TvExclusions) GenerateRecommendationsForUser(
            Guid userId,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            PluginConfiguration config)
        {
            _logger.LogDebug("Generating recommendations for user {UserId}", userId);

            try
            {
                var profile = _userProfileService.BuildUserProfile(userId, embeddings, config);
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

                var tvRecs = _recommendationEngine.GenerateRecommendations(
                    userId,
                    profile,
                    embeddings,
                    metadata,
                    config,
                    MediaType.Series,
                    config.TvRecommendationCount);
                var tvExclusions = _recommendationEngine.LastExclusionCounts;

                _logger.LogDebug(
                    "Generated recommendations for user {UserId}: {MovieCount} movies, {TvCount} TV",
                    userId,
                    movieRecs.Count,
                    tvRecs.Count);

                return (movieRecs, tvRecs, warmStart, watchedCount, profile, movieExclusions, tvExclusions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate recommendations for user {UserId}", userId);
                return (new List<ScoredRecommendation>(), new List<ScoredRecommendation>(), false, 0, null, new ExclusionCounts(), new ExclusionCounts());
            }
        }

        /// <summary>
        /// Generates recommendations for multiple users efficiently.
        /// Computes embeddings once, reuses them for all users, then writes a diagnostic log.
        /// </summary>
        /// <param name="userIds">List of user IDs to process.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="startTime">Task start time (for duration in the log).</param>
        /// <returns>Dictionary mapping user IDs to their recommendations (movies, TV).</returns>
        public Task<Dictionary<Guid, (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv)>> GenerateRecommendationsForMultipleUsersAsync(
            IReadOnlyList<Guid> userIds,
            PluginConfiguration config,
            DateTime startTime)
        {
            var results = new Dictionary<Guid, (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv)>();

            if (userIds.Count == 0)
            {
                return Task.FromResult(results);
            }

            _logger.LogInformation("Generating recommendations for {Count} users", userIds.Count);

            var (embeddings, metadata, vocabulary, libraryScan, vocabularyBuild, embeddingCompute) = ComputeEmbeddings();

            // Per-user data collected for the diagnostic log
            var userLogEntries = new List<(string Username, bool WarmStart, int WatchedCount,
                List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv,
                UserProfile? Profile, ExclusionCounts MovieExclusions, ExclusionCounts TvExclusions,
                TimeSpan UserDuration)>();

            foreach (var userId in userIds)
            {
                var userStart = DateTime.UtcNow;
                var (movies, tv, warmStart, watchedCount, profile, movieExclusions, tvExclusions) =
                    GenerateRecommendationsForUser(userId, embeddings, metadata, config);
                var userDuration = DateTime.UtcNow - userStart;

                results[userId] = (movies, tv);

                var username = _userManager.GetUserById(userId)?.Username ?? userId.ToString();
                userLogEntries.Add((username, warmStart, watchedCount, movies, tv, profile, movieExclusions, tvExclusions, userDuration));
            }

            _logger.LogInformation("Successfully generated recommendations for {Count}/{Total} users", results.Count, userIds.Count);

            if (config.EnableDiagnosticLog)
            {
                WriteDiagnosticLog(startTime, metadata, vocabulary, embeddings, userLogEntries, config, libraryScan, vocabularyBuild, embeddingCompute);
            }

            return Task.FromResult(results);
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

        private static void AppendRecommendationList(
            StringBuilder sb,
            string label,
            List<ScoredRecommendation> recs,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            bool warmStart,
            UserProfile? profile = null)
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
                    var detail = $"           cosine={rec.CosineSimilarity.Value:F3}";

                    if (rec.RatingProximity.HasValue)
                    {
                        detail += $"  rating-prox={rec.RatingProximity.Value:F3}";

                        var parts = new System.Collections.Generic.List<string>();

                        if (rec.ItemCommunityRating.HasValue)
                        {
                            var s = $"community: item={rec.ItemCommunityRating.Value:F1}";
                            if (profile?.AverageCommunityRating.HasValue == true)
                            {
                                s += $" user={profile.AverageCommunityRating.Value:F1}";
                            }

                            parts.Add(s);
                        }

                        if (rec.ItemCriticRating.HasValue)
                        {
                            var s = $"critic: item={rec.ItemCriticRating.Value:F0}";
                            if (profile?.AverageCriticRating.HasValue == true)
                            {
                                s += $" user={profile.AverageCriticRating.Value:F0}";
                            }

                            parts.Add(s);
                        }

                        if (parts.Count > 0)
                        {
                            detail += $"  [{string.Join(" | ", parts)}]";
                        }
                    }

                    sb.AppendLine(detail);
                }
            }

            sb.AppendLine();
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
                TimeSpan UserDuration)> userEntries,
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
            sb.AppendLine("CONFIG");
            sb.AppendLine($"  Movies             : {config.MovieRecommendationCount}");
            sb.AppendLine($"  TV shows           : {config.TvRecommendationCount}");
            sb.AppendLine($"  Min watched        : {config.MinWatchedItemsForPersonalization}");
            sb.AppendLine($"  Favorite boost     : {config.FavoriteBoost:F1}×");
            sb.AppendLine($"  Rewatch boost      : {config.RewatchBoost:F1}×");
            sb.AppendLine($"  Recency half-life  : {config.RecencyDecayHalfLifeDays:F0} d");
            var proximityLabel = config.EnableRatingProximity
                ? $"on ({config.RatingProximityWeight:P0} blend)"
                : "off";
            sb.AppendLine($"  Rating proximity   : {proximityLabel}");
            var actorsLabel = config.MaxVocabularyActors > 0 ? $"{config.MaxVocabularyActors} (limited)" : "unlimited";
            var directorsLabel = config.MaxVocabularyDirectors > 0 ? $"{config.MaxVocabularyDirectors} (limited)" : "unlimited";
            var tagsLabel = config.MaxVocabularyTags > 0 ? $"{config.MaxVocabularyTags} (limited)" : "unlimited";
            sb.AppendLine($"  Vocab actors       : {actorsLabel}");
            sb.AppendLine($"  Vocab directors    : {directorsLabel}");
            sb.AppendLine($"  Vocab tags         : {tagsLabel}");
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
            sb.AppendLine($"  ─────────────────");
            sb.AppendLine($"  Dimensions : {embeddingDim,4}");
            sb.AppendLine();

            // TIMING
            sb.AppendLine("TIMING");
            sb.AppendLine($"  Library scan  : {libraryScan.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Vocabulary    : {vocabularyBuild.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Embeddings    : {embeddingCompute.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Per user      : {perUserTotal.TotalSeconds,6:F2} s");
            sb.AppendLine($"  Total         : {totalDuration.TotalSeconds,6:F2} s");
            sb.AppendLine();

            foreach (var (username, warmStart, watchedCount, movies, tv, profile, movieExclusions, tvExclusions, userDuration) in userEntries)
            {
                sb.AppendLine(dash);
                sb.AppendLine($"USER: {username}  ({userDuration.TotalSeconds:F2} s)");

                var profileLabel = warmStart
                    ? $"warm-start ({watchedCount} watched items)"
                    : $"cold-start ({watchedCount} watched items, threshold: {config.MinWatchedItemsForPersonalization})";
                sb.AppendLine($"  Profile : {profileLabel}");

                if (warmStart && profile != null)
                {
                    var communityStr = profile.AverageCommunityRating.HasValue
                        ? $"community avg={profile.AverageCommunityRating.Value:F1} (±{profile.CommunityRatingStdDev:F1})"
                        : "community avg=n/a";
                    var criticStr = profile.AverageCriticRating.HasValue
                        ? $"critic avg={profile.AverageCriticRating.Value:F0} (±{profile.CriticRatingStdDev:F0})"
                        : "critic avg=n/a";
                    sb.AppendLine($"  Ratings : {communityStr}  {criticStr}");
                }

                if (warmStart)
                {
                    sb.AppendLine($"  Exclusions (movies): {movieExclusions.Watched} watched, {movieExclusions.InProgress} in-progress, {movieExclusions.Inaccessible} inaccessible, {movieExclusions.NoMetadata} no-metadata, {movieExclusions.NotFound} not-found → {movieExclusions.Final} candidates");
                    AppendExclusionItems(sb, "no-metadata", movieExclusions.NoMetadataItems);
                    AppendExclusionItems(sb, "not-found", movieExclusions.NotFoundItems);
                    sb.AppendLine($"  Exclusions (TV)    : {tvExclusions.SeriesWatched} watched, {tvExclusions.InProgress} in-progress, {tvExclusions.Inaccessible} inaccessible, {tvExclusions.NoMetadata} no-metadata, {tvExclusions.NotFound} not-found → {tvExclusions.Final} candidates");
                    AppendExclusionItems(sb, "no-metadata", tvExclusions.NoMetadataItems);
                    AppendExclusionItems(sb, "not-found", tvExclusions.NotFoundItems);

                    if (profile != null)
                    {
                        var topFeatures = GetTopTasteFeatures(profile.TasteVector, vocabulary);
                        if (topFeatures.Count > 0)
                        {
                            sb.AppendLine($"  Top taste signals:");
                            for (var i = 0; i < topFeatures.Count; i++)
                            {
                                sb.AppendLine($"    {i + 1,3}.  {topFeatures[i].Weight:F3}  {topFeatures[i].Label}");
                            }
                        }
                    }
                }

                sb.AppendLine();

                AppendRecommendationList(sb, "Movies", movies, metadata, warmStart, profile);
                AppendRecommendationList(sb, "TV Shows", tv, metadata, warmStart, profile);
            }

            sb.AppendLine(line);

            _diagnosticLogService.Save(sb.ToString());
        }
    }
}
