using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

using LocalMediaType = Jellyfin.Plugin.LocalRecs.Models.MediaType;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Service for generating personalized recommendations.
    /// Scores candidates using cosine similarity between user taste vectors and item embeddings.
    /// </summary>
    public class RecommendationEngine
    {
        /// <summary>
        /// How many times <c>maxResults</c> worth of top-relevance candidates diversity re-ranking is
        /// allowed to pick from. Bounds the pool so diversity can never reach past a reasonable
        /// relevance floor to select something with little to no real taste match just because it's
        /// maximally different from everything already selected — see <see cref="ApplyDiversityReranking"/>.
        /// </summary>
        private const int DiversityCandidatePoolMultiplier = 3;

        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<RecommendationEngine> _logger;
        private ExclusionCounts _lastExclusionCounts = new ExclusionCounts();
        private ScoreDistribution _lastScoreDistribution = new ScoreDistribution();

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationEngine"/> class.
        /// </summary>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger.</param>
        public RecommendationEngine(
            IUserDataManager userDataManager,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger<RecommendationEngine> logger)
        {
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Gets the exclusion counts from the most recent <see cref="GenerateRecommendations"/> call.</summary>
        public ExclusionCounts LastExclusionCounts => _lastExclusionCounts;

        /// <summary>Gets the score distribution from the most recent <see cref="GenerateRecommendations"/> call.</summary>
        public ScoreDistribution LastScoreDistribution => _lastScoreDistribution;

        /// <summary>
        /// Generates recommendations for a user.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="userProfile">The user's taste profile.</param>
        /// <param name="embeddings">Dictionary of item embeddings.</param>
        /// <param name="metadata">Dictionary of item metadata.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="mediaType">Filter to specific media type (null = all types).</param>
        /// <param name="maxResults">Maximum number of recommendations to return.</param>
        /// <returns>List of scored recommendations, ordered by score descending.</returns>
        /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
        /// <exception cref="ArgumentException">Thrown when embeddings or metadata are empty.</exception>
        public List<ScoredRecommendation> GenerateRecommendations(
            Guid userId,
            UserProfile? userProfile,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            PluginConfiguration config,
            LocalMediaType? mediaType = null,
            int maxResults = 25)
        {
            if (embeddings == null)
            {
                throw new ArgumentNullException(nameof(embeddings));
            }

            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (embeddings.Count == 0)
            {
                throw new ArgumentException("Embeddings dictionary cannot be empty", nameof(embeddings));
            }

            if (metadata.Count == 0)
            {
                throw new ArgumentException("Metadata dictionary cannot be empty", nameof(metadata));
            }

            _logger.LogDebug(
                "Generating recommendations for user {UserId}, mediaType: {MediaType}, max: {MaxResults}",
                userId,
                mediaType?.ToString() ?? "All",
                maxResults);

            _lastExclusionCounts = new ExclusionCounts();
            _lastScoreDistribution = new ScoreDistribution();

            // Check for cold-start scenario
            if (userProfile == null || userProfile.WatchedItemCount < config.MinWatchedItemsForPersonalization)
            {
                _logger.LogDebug(
                    "Cold-start scenario for user {UserId}: {WatchedCount} watched items (min: {MinRequired})",
                    userId,
                    userProfile?.WatchedItemCount ?? 0,
                    config.MinWatchedItemsForPersonalization);

                return GenerateColdStartRecommendations(userId, metadata, mediaType, maxResults);
            }

            // Get unwatched candidates
            var candidates = GetUnwatchedCandidates(userId, embeddings.Keys, metadata, mediaType, config);

            if (candidates.Count == 0)
            {
                _logger.LogWarning("No unwatched candidates found for user {UserId}", userId);
                return new List<ScoredRecommendation>();
            }

            _logger.LogDebug(
                "Found {CandidateCount} unwatched candidates for user {UserId}",
                candidates.Count,
                userId);

            // Score all candidates
            var scoredCandidates = new List<ScoredRecommendation>();

            foreach (var candidateId in candidates)
            {
                if (!embeddings.TryGetValue(candidateId, out var embedding))
                {
                    continue; // Skip if no embedding
                }

                if (!metadata.TryGetValue(candidateId, out var itemMetadata))
                {
                    continue; // Skip if no metadata
                }

                var score = ScoreCandidate(userProfile, embedding, itemMetadata, config);

                scoredCandidates.Add(score);
            }

            // Compute score distribution before truncation
            if (scoredCandidates.Count > 0)
            {
                var mean = scoredCandidates.Average(s => s.Score);
                var stdDev = (float)Math.Sqrt(scoredCandidates.Average(s => Math.Pow(s.Score - mean, 2)));
                _lastScoreDistribution = new ScoreDistribution
                {
                    CandidateCount = scoredCandidates.Count,
                    MinScore = scoredCandidates.Min(s => s.Score),
                    MaxScore = scoredCandidates.Max(s => s.Score),
                    MeanScore = mean,
                    StdDev = stdDev
                };
            }

            // Sort by score descending and take top N, optionally trading some relevance for
            // variety so franchise/sequel entries don't dominate the list.
            var recommendations = config.EnableDiversityReranking
                ? ApplyDiversityReranking(scoredCandidates, embeddings, maxResults, config.DiversityWeight)
                : scoredCandidates.OrderByDescending(r => r.Score).Take(maxResults).ToList();

            _logger.LogDebug(
                "Generated {RecommendationCount} recommendations for user {UserId}",
                recommendations.Count,
                userId);

            return recommendations;
        }

        /// <summary>
        /// Gets the set of item IDs that a user has access to based on their library permissions.
        /// Uses Jellyfin's built-in user-scoped query which respects library access settings.
        /// </summary>
        /// <param name="user">The Jellyfin user.</param>
        /// <returns>HashSet of accessible item IDs.</returns>
        private HashSet<Guid> GetUserAccessibleItemIds(Jellyfin.Database.Implementations.Entities.User user)
        {
            var accessibleItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true
            });

            if (accessibleItems == null)
            {
                return new HashSet<Guid>();
            }

            return accessibleItems.Select(i => i.Id).ToHashSet();
        }

        /// <summary>
        /// Gets unwatched candidate items for a user.
        /// Excludes fully watched items, optionally partially watched series,
        /// and items from libraries the user cannot access.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="availableItemIds">Available item IDs from embeddings.</param>
        /// <param name="metadata">Item metadata dictionary.</param>
        /// <param name="mediaType">Filter to specific media type.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>List of unwatched item IDs.</returns>
        private List<Guid> GetUnwatchedCandidates(
            Guid userId,
            IEnumerable<Guid> availableItemIds,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            LocalMediaType? mediaType,
            PluginConfiguration config)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return new List<Guid>();
            }

            // Get items accessible to this user based on library permissions
            var accessibleItemIds = GetUserAccessibleItemIds(user);
            _logger.LogDebug(
                "User {UserId} has access to {Count} items",
                userId,
                accessibleItemIds.Count);

            var candidates = new List<Guid>();
            int excInaccessible = 0, excNoMetadata = 0, excNotFound = 0,
                excWatched = 0, excSeriesWatched = 0, excInProgress = 0;
            var noMetadataItems = new List<string>();
            var notFoundItems = new List<string>();

            foreach (var itemId in availableItemIds)
            {
                // Get item metadata for type filtering
                if (!metadata.TryGetValue(itemId, out var itemMetadata))
                {
                    continue;
                }

                // Filter by media type if specified
                if (mediaType.HasValue && itemMetadata.Type != mediaType.Value)
                {
                    continue;
                }

                // Exclude items from libraries the user cannot access
                if (!accessibleItemIds.Contains(itemId))
                {
                    excInaccessible++;
                    continue;
                }

                // Exclude items with insufficient metadata (no genres AND no actors)
                // These produce unreliable similarity scores
                if (itemMetadata.Genres.Count == 0 && itemMetadata.Actors.Count == 0)
                {
                    noMetadataItems.Add(itemMetadata.Name);
                    excNoMetadata++;
                    continue;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    _logger.LogDebug(
                        "Item not found in library: {ItemId} ({Name})",
                        itemId,
                        itemMetadata.Name);
                    notFoundItems.Add(itemMetadata.Name);
                    excNotFound++;
                    continue;
                }

                // Log if item path suggests it's from virtual library (shouldn't happen but check)
                if (itemMetadata.Type == LocalMediaType.Series && item.Path != null && item.Path.Contains("virtual-libraries"))
                {
                    _logger.LogWarning(
                        "Virtual library item in candidates: {Name} (ItemId={ItemId}, Path={Path})",
                        itemMetadata.Name,
                        itemId,
                        item.Path);
                }

                var userData = _userDataManager.GetUserData(user, item);

                // Exclude fully watched items
                // For series, userData.Played is not reliable - we need to check episode watch status
                if (itemMetadata.Type == LocalMediaType.Series && item is Series series)
                {
                    // Exclude series with any watched episodes (both in-progress and fully watched)
                    if (HasAnyWatchedEpisodes(series, user))
                    {
                        _logger.LogDebug(
                            "Excluding series with watch history: {Name}",
                            itemMetadata.Name);
                        excSeriesWatched++;
                        continue;
                    }
                }
                else if (userData != null && userData.Played)
                {
                    _logger.LogDebug(
                        "Excluding watched item: {Name} (Played={Played})",
                        itemMetadata.Name,
                        userData.Played);
                    excWatched++;
                    continue;
                }

                // Exclude items with any playback progress (user is currently watching or has started)
                // These items will be removed from virtual library by PlayStatusSyncService
                // and should not be re-added to recommendations until fully unwatched
                if (userData != null && userData.PlaybackPositionTicks > 0)
                {
                    excInProgress++;
                    continue;
                }

                candidates.Add(itemId);
            }

            _lastExclusionCounts = new ExclusionCounts
            {
                Inaccessible = excInaccessible,
                NoMetadata = excNoMetadata,
                NoMetadataItems = noMetadataItems,
                NotFound = excNotFound,
                NotFoundItems = notFoundItems,
                Watched = excWatched,
                SeriesWatched = excSeriesWatched,
                InProgress = excInProgress,
                Final = candidates.Count
            };

            return candidates;
        }

        /// <summary>
        /// Checks if a series has any watched episodes.
        /// Series with any watch history (in-progress or fully watched) should be excluded
        /// from recommendations since the user has already engaged with them.
        /// </summary>
        /// <param name="series">The series to check.</param>
        /// <param name="user">The user to check watch status for.</param>
        /// <returns>True if the series has at least one watched episode.</returns>
        private bool HasAnyWatchedEpisodes(Series series, Jellyfin.Database.Implementations.Entities.User user)
        {
            // Query for any watched episodes in this series
            var watchedEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsPlayed = true,
                Limit = 1, // We only need to know if any exist
                Recursive = true
            });

            return watchedEpisodes.Count > 0;
        }

        /// <summary>
        /// Scores a candidate item against the user's taste profile using cosine similarity
        /// and optionally rating proximity.
        /// </summary>
        /// <param name="userProfile">The user's taste profile.</param>
        /// <param name="candidateEmbedding">The candidate item's embedding.</param>
        /// <param name="itemMetadata">The candidate item's metadata.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Scored recommendation.</returns>
        private ScoredRecommendation ScoreCandidate(
            UserProfile userProfile,
            ItemEmbedding candidateEmbedding,
            MediaItemMetadata itemMetadata,
            PluginConfiguration config)
        {
            var score = TasteScoring.Compute(
                userProfile.TasteVector,
                userProfile.AverageCommunityRating,
                userProfile.AverageCriticRating,
                candidateEmbedding.Vector,
                itemMetadata.CommunityRating,
                itemMetadata.CriticRating,
                config.EnableRatingProximity,
                config.RatingProximityWeight);

            return new ScoredRecommendation(candidateEmbedding.ItemId, score.FinalScore)
            {
                CosineSimilarity = score.CosineSimilarity,
                CommunityProximity = score.CommunityProximity,
                CriticProximity = score.CriticProximity,
                RatingProximity = score.RatingProximity,
                ItemCommunityRating = itemMetadata.CommunityRating,
                ItemCriticRating = itemMetadata.CriticRating
            };
        }

        /// <summary>
        /// Selects the top <paramref name="maxResults"/> candidates using greedy Maximal Marginal
        /// Relevance: each pick after the first trades off relevance (<see cref="ScoredRecommendation.Score"/>,
        /// min-max normalized within the pool) against similarity to items already selected, so
        /// franchise/sequel clusters that share heavy actor/genre/tag overlap don't dominate the list
        /// the way plain score-descending selection allows. Normalizing relevance before blending it
        /// with the diversity penalty keeps a given weight behaving consistently regardless of whether
        /// the pool's raw scores happen to be widely spread or naturally compressed (e.g. a user's TV
        /// candidates all weakly matching a narrow taste profile) — without it, a compressed pool lets
        /// the penalty dominate far more than the configured weight implies. The pool MMR picks from is
        /// also first bounded to the top <see cref="DiversityCandidatePoolMultiplier"/> ×
        /// <paramref name="maxResults"/> candidates by relevance, as a hard backstop so diversity can
        /// never reach a candidate with little to no real taste match purely because it's "different"
        /// from what's already picked. Every candidate is guaranteed to have an entry in
        /// <paramref name="embeddings"/>, since <paramref name="scoredCandidates"/> is only ever built
        /// from candidates that already passed an embeddings lookup.
        /// </summary>
        /// <param name="scoredCandidates">All scored candidates for this request.</param>
        /// <param name="embeddings">Item embeddings, used to compute similarity between candidates.</param>
        /// <param name="maxResults">Maximum number of recommendations to return.</param>
        /// <param name="diversityWeight">0.0 = pure relevance (matches plain score-descending selection), 1.0 = pure diversity.</param>
        /// <returns>Selected recommendations in pick order.</returns>
        private List<ScoredRecommendation> ApplyDiversityReranking(
            List<ScoredRecommendation> scoredCandidates,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            int maxResults,
            double diversityWeight)
        {
            var poolSize = maxResults * DiversityCandidatePoolMultiplier;
            var pool = scoredCandidates.Count > poolSize
                ? scoredCandidates.OrderByDescending(r => r.Score).Take(poolSize).ToList()
                : scoredCandidates;

            // Min-max normalize relevance within the pool before blending with the diversity
            // penalty. Without this, a pool whose raw scores are naturally compressed (e.g. a
            // user's TV candidates all weakly matching a narrow taste profile) lets the diversity
            // term dominate far more than the configured weight implies, since the penalty and
            // the relevance signal aren't on comparable scales. Normalizing means a given weight
            // behaves consistently regardless of how spread out or compressed the pool's raw
            // scores happen to be.
            var poolMin = pool.Min(r => r.Score);
            var poolRange = pool.Max(r => r.Score) - poolMin;

            float NormalizedScore(ScoredRecommendation r) =>
                poolRange > 0f ? (r.Score - poolMin) / poolRange : 0f;

            var selected = new List<ScoredRecommendation>();
            var remaining = new List<ScoredRecommendation>(pool);
            var maxSimToSelected = new Dictionary<Guid, float>(remaining.Count);
            foreach (var candidate in remaining)
            {
                maxSimToSelected[candidate.ItemId] = 0f;
            }

            while (selected.Count < maxResults && remaining.Count > 0)
            {
                // Everyone left gets in regardless of order — no need to run MMR scoring.
                if (remaining.Count <= maxResults - selected.Count)
                {
                    selected.AddRange(remaining.OrderByDescending(r => r.Score));
                    break;
                }

                ScoredRecommendation best;
                if (selected.Count == 0)
                {
                    // Seed pick: pure relevance, nothing to compare against yet.
                    best = remaining.OrderByDescending(r => r.Score).First();
                }
                else
                {
                    best = remaining
                        .OrderByDescending(r => ((1 - diversityWeight) * NormalizedScore(r)) - (diversityWeight * maxSimToSelected[r.ItemId]))
                        .ThenByDescending(r => r.Score)
                        .First();
                }

                remaining.Remove(best);
                selected.Add(best);

                var bestVector = embeddings[best.ItemId].Vector;
                foreach (var candidate in remaining)
                {
                    var sim = VectorMath.CosineSimilarity(bestVector, embeddings[candidate.ItemId].Vector);
                    if (sim > maxSimToSelected[candidate.ItemId])
                    {
                        maxSimToSelected[candidate.ItemId] = sim;
                    }
                }
            }

            return selected;
        }

        /// <summary>
        /// Generates recommendations for users with insufficient watch history (cold-start).
        /// Returns top-rated items from the library.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="metadata">Item metadata dictionary.</param>
        /// <param name="mediaType">Filter to specific media type.</param>
        /// <param name="maxResults">Maximum number of recommendations.</param>
        /// <returns>List of top-rated items.</returns>
        private List<ScoredRecommendation> GenerateColdStartRecommendations(
            Guid userId,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            LocalMediaType? mediaType,
            int maxResults)
        {
            _logger.LogDebug(
                "Generating cold-start recommendations for user {UserId}",
                userId);

            // Filter to media type if specified
            var candidateMetadata = metadata.Values.AsEnumerable();

            if (mediaType.HasValue)
            {
                candidateMetadata = candidateMetadata.Where(m => m.Type == mediaType.Value);
            }

            // Get unwatched items that the user has access to
            var user = _userManager.GetUserById(userId);
            var unwatchedCandidates = new List<MediaItemMetadata>();

            if (user != null)
            {
                // Filter to items from libraries the user can access
                var accessibleItemIds = GetUserAccessibleItemIds(user);

                foreach (var item in candidateMetadata)
                {
                    // Exclude items from inaccessible libraries
                    if (!accessibleItemIds.Contains(item.Id))
                    {
                        continue;
                    }

                    var libraryItem = _libraryManager.GetItemById(item.Id);
                    if (libraryItem == null)
                    {
                        continue;
                    }

                    var userData = _userDataManager.GetUserData(user, libraryItem);
                    if (userData != null && userData.Played)
                    {
                        continue; // Skip watched items
                    }

                    unwatchedCandidates.Add(item);
                }
            }
            else
            {
                unwatchedCandidates = candidateMetadata.ToList();
            }

            // Sort by community rating (primary) and critic rating (secondary)
            // Normalize scores to [0-1] range to match personalized recommendation scores
            var topRated = unwatchedCandidates
                .OrderByDescending(m => m.CommunityRating ?? 0)
                .ThenByDescending(m => m.CriticRating ?? 0)
                .Take(maxResults)
                .Select(m => new ScoredRecommendation(m.Id, (m.CommunityRating ?? 0) / 10.0f)
                {
                    ItemCommunityRating = m.CommunityRating,
                    ItemCriticRating = m.CriticRating
                })
                .ToList();

            _logger.LogDebug(
                "Generated {Count} cold-start recommendations for user {UserId}",
                topRated.Count,
                userId);

            return topRated;
        }
    }
}
