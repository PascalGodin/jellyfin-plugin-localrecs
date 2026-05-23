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
        /// <returns>Movie and TV recommendations, warm-start flag, watched item count, user profile, exclusion counts, and score distributions.</returns>
        public (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv, bool WarmStart, int WatchedItemCount, UserProfile? Profile, ExclusionCounts MovieExclusions, ExclusionCounts TvExclusions, ScoreDistribution MovieScoreDist, ScoreDistribution TvScoreDist) GenerateRecommendationsForUser(
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

                return (movieRecs, tvRecs, warmStart, watchedCount, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate recommendations for user {UserId}", userId);
                return (new List<ScoredRecommendation>(), new List<ScoredRecommendation>(), false, 0, null, new ExclusionCounts(), new ExclusionCounts(), new ScoreDistribution(), new ScoreDistribution());
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
                ScoreDistribution MovieScoreDist, ScoreDistribution TvScoreDist,
                TimeSpan UserDuration)>();

            foreach (var userId in userIds)
            {
                var userStart = DateTime.UtcNow;
                var (movies, tv, warmStart, watchedCount, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist) =
                    GenerateRecommendationsForUser(userId, embeddings, metadata, config);
                var userDuration = DateTime.UtcNow - userStart;

                results[userId] = (movies, tv);

                var username = _userManager.GetUserById(userId)?.Username ?? userId.ToString();
                userLogEntries.Add((username, warmStart, watchedCount, movies, tv, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist, userDuration));
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
            sb.AppendLine($"  Play count cap     : {config.MaxPlayCountForWeighting}");
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

            foreach (var (username, warmStart, watchedCount, movies, tv, profile, movieExclusions, tvExclusions, movieScoreDist, tvScoreDist, userDuration) in userEntries)
            {
                sb.AppendLine(dash);
                sb.AppendLine($"USER: {username}  ({userDuration.TotalSeconds:F2} s)");

                var profileLabel = warmStart
                    ? $"personalized ({watchedCount} items watched)"
                    : $"cold-start — rating-based ({watchedCount} items, threshold: {config.MinWatchedItemsForPersonalization})";
                sb.AppendLine($"  Profile : {profileLabel}");

                if (warmStart && profile != null)
                {
                    var communityStr = profile.AverageCommunityRating.HasValue
                        ? $"community {profile.AverageCommunityRating.Value:F1}/10 (±{profile.CommunityRatingStdDev:F1})"
                        : "community n/a";
                    var criticStr = profile.AverageCriticRating.HasValue
                        ? $"critic {profile.AverageCriticRating.Value:F0}/100 (±{profile.CriticRatingStdDev:F0})"
                        : "critic n/a";
                    sb.AppendLine($"  Ratings : {communityStr}  {criticStr}");

                    if (profile.TopWatchContributions.Count > 0)
                    {
                        var maxContribWeight = profile.TopWatchContributions[0].Weight;
                        sb.AppendLine($"  Top watched (by taste weight):");
                        for (var i = 0; i < profile.TopWatchContributions.Count; i++)
                        {
                            var c = profile.TopWatchContributions[i];
                            metadata.TryGetValue(c.ItemId, out var watchMeta);
                            var watchName = watchMeta?.Name ?? c.ItemId.ToString();
                            var watchYear = watchMeta?.ReleaseYear > 0 ? $" ({watchMeta.ReleaseYear})" : string.Empty;
                            var flags = new System.Collections.Generic.List<string>();
                            if (c.IsFavorite)
                            {
                                flags.Add("favorite");
                            }

                            if (c.PlayCount > 1)
                            {
                                flags.Add($"{c.PlayCount}× watched");
                            }

                            var age = c.DaysSince < 365 ? $"{c.DaysSince:F0}d ago" : $"{c.DaysSince / 365:F1}y ago";
                            flags.Add(age);
                            var contribPct = maxContribWeight > 0 ? c.Weight / maxContribWeight * 100 : 0;
                            sb.AppendLine($"    {i + 1,3}.  {contribPct,3:F0}%  {watchName}{watchYear}  [{string.Join("  ", flags)}]");
                        }
                    }
                }

                if (warmStart)
                {
                    var movieTopRatio = movieScoreDist.MeanScore > 0 ? movieScoreDist.MaxScore / movieScoreDist.MeanScore : 0f;
                    var tvTopRatio = tvScoreDist.MeanScore > 0 ? tvScoreDist.MaxScore / tvScoreDist.MeanScore : 0f;
                    sb.AppendLine($"  Score dist (movies): {movieScoreDist.CandidateCount} candidates  min={movieScoreDist.MinScore:F3} max={movieScoreDist.MaxScore:F3} mean={movieScoreDist.MeanScore:F3} spread={movieScoreDist.StdDev:F3}  (top {movieTopRatio:F1}× mean)");
                    sb.AppendLine($"  Score dist (TV)    : {tvScoreDist.CandidateCount} candidates  min={tvScoreDist.MinScore:F3} max={tvScoreDist.MaxScore:F3} mean={tvScoreDist.MeanScore:F3} spread={tvScoreDist.StdDev:F3}  (top {tvTopRatio:F1}× mean)");
                    sb.AppendLine(FormatExclusionLine("movies", movieExclusions.Watched, movieExclusions.InProgress, movieExclusions.Inaccessible, movieExclusions.NoMetadata, movieExclusions.NotFound, movieExclusions.Final));
                    AppendExclusionItems(sb, "no-metadata", movieExclusions.NoMetadataItems);
                    AppendExclusionItems(sb, "not-found", movieExclusions.NotFoundItems);
                    sb.AppendLine(FormatExclusionLine("TV", tvExclusions.SeriesWatched, tvExclusions.InProgress, tvExclusions.Inaccessible, tvExclusions.NoMetadata, tvExclusions.NotFound, tvExclusions.Final));
                    AppendExclusionItems(sb, "no-metadata", tvExclusions.NoMetadataItems);
                    AppendExclusionItems(sb, "not-found", tvExclusions.NotFoundItems);

                    if (profile != null)
                    {
                        var topFeatures = GetTopTasteFeatures(profile.TasteVector, vocabulary);
                        if (topFeatures.Count > 0)
                        {
                            var maxTasteWeight = topFeatures[0].Weight;
                            sb.AppendLine($"  Top taste signals:");
                            for (var i = 0; i < topFeatures.Count; i++)
                            {
                                var tastePct = maxTasteWeight > 0 ? topFeatures[i].Weight / maxTasteWeight * 100 : 0;
                                sb.AppendLine($"    {i + 1,3}.  {tastePct,3:F0}%  {topFeatures[i].Label}");
                            }
                        }
                    }
                }

                sb.AppendLine();

                AppendRecommendationList(sb, "Movies", movies, metadata, warmStart, profile, embeddings, vocabulary);
                AppendRecommendationList(sb, "TV Shows", tv, metadata, warmStart, profile, embeddings, vocabulary);
            }

            sb.AppendLine(line);

            _diagnosticLogService.Save(sb.ToString());
        }
    }
}
