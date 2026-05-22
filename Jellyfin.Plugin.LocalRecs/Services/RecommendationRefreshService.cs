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
        /// <returns>Tuple of embeddings, metadata, and vocabulary.</returns>
        public (IReadOnlyDictionary<Guid, ItemEmbedding> Embeddings, IReadOnlyDictionary<Guid, MediaItemMetadata> Metadata, FeatureVocabulary Vocabulary) ComputeEmbeddings()
        {
            var library = _libraryAnalysisService.GetAllMediaItems();
            var metadata = library.ToDictionary(m => m.Id);

            _logger.LogDebug("Computing fresh embeddings for {Count} items", library.Count);

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            var vocabulary = _vocabularyBuilder.BuildVocabulary(
                library,
                config.MaxVocabularyActors,
                config.MaxVocabularyDirectors,
                config.MaxVocabularyTags);
            var embeddings = _embeddingService.ComputeEmbeddings(library, vocabulary);

            var embeddingsDict = embeddings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return (embeddingsDict, metadata, vocabulary);
        }

        /// <summary>
        /// Generates recommendations for a single user.
        /// </summary>
        public (List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv, bool WarmStart, int WatchedItemCount) GenerateRecommendationsForUser(
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

                var tvRecs = _recommendationEngine.GenerateRecommendations(
                    userId,
                    profile,
                    embeddings,
                    metadata,
                    config,
                    MediaType.Series,
                    config.TvRecommendationCount);

                _logger.LogDebug(
                    "Generated recommendations for user {UserId}: {MovieCount} movies, {TvCount} TV",
                    userId,
                    movieRecs.Count,
                    tvRecs.Count);

                return (movieRecs, tvRecs, warmStart, watchedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate recommendations for user {UserId}", userId);
                return (new List<ScoredRecommendation>(), new List<ScoredRecommendation>(), false, 0);
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

            var (embeddings, metadata, vocabulary) = ComputeEmbeddings();

            // Per-user data collected for the diagnostic log
            var userLogEntries = new List<(string Username, bool WarmStart, int WatchedCount,
                List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv)>();

            foreach (var userId in userIds)
            {
                var (movies, tv, warmStart, watchedCount) = GenerateRecommendationsForUser(userId, embeddings, metadata, config);
                results[userId] = (movies, tv);

                var username = _userManager.GetUserById(userId)?.Username ?? userId.ToString();
                userLogEntries.Add((username, warmStart, watchedCount, movies, tv));
            }

            _logger.LogInformation("Successfully generated recommendations for {Count}/{Total} users", results.Count, userIds.Count);

            WriteDiagnosticLog(startTime, metadata, vocabulary, embeddings, userLogEntries, config);

            return Task.FromResult(results);
        }

        private void WriteDiagnosticLog(
            DateTime startTime,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            FeatureVocabulary vocabulary,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            List<(string Username, bool WarmStart, int WatchedCount, List<ScoredRecommendation> Movies, List<ScoredRecommendation> Tv)> userEntries,
            PluginConfiguration config)
        {
            var duration = DateTime.UtcNow - startTime;
            var movieCount = metadata.Values.Count(m => m.Type == MediaType.Movie);
            var seriesCount = metadata.Values.Count(m => m.Type == MediaType.Series);
            var embeddingDim = embeddings.Values.FirstOrDefault()?.Vector.Length ?? 0;

            var sb = new StringBuilder();
            var line = new string('=', 80);
            var dash = new string('─', 80);

            sb.AppendLine(line);
            sb.AppendLine("LocalRecs Diagnostic Log");
            sb.AppendLine($"Run timestamp : {startTime:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Duration      : {duration.TotalSeconds:F2} s");
            sb.AppendLine(line);
            sb.AppendLine();

            sb.AppendLine("LIBRARY");
            sb.AppendLine($"  Movies  : {movieCount}");
            sb.AppendLine($"  Series  : {seriesCount}");
            sb.AppendLine($"  Total   : {metadata.Count}");
            sb.AppendLine();

            sb.AppendLine("VOCABULARY");
            sb.AppendLine($"  Genres     : {vocabulary.Genres.Count,4}");
            sb.AppendLine($"  Actors     : {vocabulary.Actors.Count,4}");
            sb.AppendLine($"  Directors  : {vocabulary.Directors.Count,4}");
            sb.AppendLine($"  Tags       : {vocabulary.Tags.Count,4}");
            sb.AppendLine($"  Decades    : {vocabulary.Decades.Count,4}");
            sb.AppendLine($"  ─────────────────");
            sb.AppendLine($"  Dimensions : {embeddingDim,4}");
            sb.AppendLine();

            foreach (var (username, warmStart, watchedCount, movies, tv) in userEntries)
            {
                sb.AppendLine(dash);
                sb.AppendLine($"USER: {username}");

                var profileLabel = warmStart
                    ? $"warm-start ({watchedCount} watched items)"
                    : $"cold-start ({watchedCount} watched items, threshold: {config.MinWatchedItemsForPersonalization})";
                sb.AppendLine($"  Profile : {profileLabel}");
                sb.AppendLine();

                AppendRecommendationList(sb, "Movies", movies, metadata, warmStart);
                AppendRecommendationList(sb, "TV Shows", tv, metadata, warmStart);
            }

            sb.AppendLine(line);

            _diagnosticLogService.Save(sb.ToString());
        }

        private static void AppendRecommendationList(
            StringBuilder sb,
            string label,
            List<ScoredRecommendation> recs,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            bool warmStart)
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
            }

            sb.AppendLine();
        }
    }
}
