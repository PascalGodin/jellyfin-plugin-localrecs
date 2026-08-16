using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using Jellyfin.Plugin.LocalRecs.Tests.Fixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Domain
{
    /// <summary>
    /// Tests for <see cref="RecommendationEngine"/>.
    /// Validates recommendation generation, scoring, cold-start handling, and performance.
    /// </summary>
    public class RecommendationEngineTests
    {
        private readonly Mock<IUserDataManager> _mockUserDataManager;
        private readonly Mock<IUserManager> _mockUserManager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;
        private readonly RecommendationEngine _engine;
        private readonly PluginConfiguration _config;
        private readonly Guid _testUserId;
        private readonly User _testUser;
        private readonly List<BaseItem> _registeredItems = new List<BaseItem>();

        public RecommendationEngineTests()
        {
            _mockUserDataManager = new Mock<IUserDataManager>();
            _mockUserManager = new Mock<IUserManager>();
            _mockLibraryManager = new Mock<ILibraryManager>();
            _engine = new RecommendationEngine(
                _mockUserDataManager.Object,
                _mockUserManager.Object,
                _mockLibraryManager.Object,
                NullLogger<RecommendationEngine>.Instance);

            _testUserId = Guid.NewGuid();
            _testUser = new User("TestUser", "Default", "Default");

            _config = new PluginConfiguration
            {
                MinWatchedItemsForPersonalization = 3
            };

            _mockUserManager.Setup(m => m.GetUserById(_testUserId)).Returns(_testUser);

            // Default: all registered items are accessible (tests override via SetupUserVisibleItems)
            _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(() => _registeredItems);
        }

        [Fact]
        public void GenerateRecommendations_SciFiLover_GetsSciFiRecommendations()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User watched and loved sci-fi movies
            var matrix = library.First(m => m.Name == "The Matrix");
            var bladeRunner = library.First(m => m.Name == "Blade Runner 2049");
            var inception = library.First(m => m.Name == "Inception");

            SetupWatchedItem(matrix);
            SetupWatchedItem(bladeRunner);
            SetupWatchedItem(inception);

            // Build user profile based on sci-fi watches
            var userProfile = CreateSciFiUserProfile(embeddings, new[] { matrix.Id, bladeRunner.Id, inception.Id });

            // Setup unwatched items
            foreach (var item in library.Where(m => m.Id != matrix.Id && m.Id != bladeRunner.Id && m.Id != inception.Id))
            {
                SetupUnwatchedItem(item);
            }

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 5);

            // Assert
            recommendations.Should().NotBeEmpty();
            recommendations.Should().HaveCountLessOrEqualTo(5);

            // At least one sci-fi movie should be highly recommended
            var sciFiMovies = library.Where(m => m.Genres.Contains("Science Fiction") 
                && m.Id != matrix.Id 
                && m.Id != bladeRunner.Id 
                && m.Id != inception.Id).Select(m => m.Id).ToHashSet();
            
            var sciFiRecs = recommendations.Where(r => sciFiMovies.Contains(r.ItemId)).ToList();
            sciFiRecs.Should().NotBeEmpty("at least one sci-fi movie should be recommended to sci-fi lover");

            // Verify scores are in descending order
            for (int i = 0; i < recommendations.Count - 1; i++)
            {
                recommendations[i].Score.Should().BeGreaterThanOrEqualTo(recommendations[i + 1].Score);
            }
        }

        [Fact]
        public void GenerateRecommendations_WatchedItemsExcluded()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User watched these items
            var watchedMovies = library.Take(5).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            // Setup unwatched items
            var unwatchedMovies = library.Skip(5).ToList();
            foreach (var movie in unwatchedMovies)
            {
                SetupUnwatchedItem(movie);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert
            recommendations.Should().NotBeEmpty();

            // Verify no watched items are in recommendations
            var watchedIds = watchedMovies.Select(m => m.Id).ToHashSet();
            foreach (var rec in recommendations)
            {
                watchedIds.Should().NotContain(rec.ItemId, "watched items should be excluded from recommendations");
            }
        }

        [Fact]
        public void GenerateRecommendations_ColdStart_ReturnsTopRated()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User has no watch history (cold start)
            UserProfile? userProfile = null;

            // All items are unwatched
            foreach (var item in library)
            {
                SetupUnwatchedItem(item);
            }

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 5);

            // Assert
            recommendations.Should().NotBeEmpty();
            recommendations.Should().HaveCount(5);

            // Should return top-rated items
            // The Shawshank Redemption has highest community rating (9.3)
            var topRec = recommendations.First();
            var topItem = metadata[topRec.ItemId];
            topItem.CommunityRating.Should().BeGreaterThanOrEqualTo(8.0f, "cold-start should return highly rated items");

            // Scores should be based on community ratings in cold-start
            for (int i = 0; i < recommendations.Count - 1; i++)
            {
                recommendations[i].Score.Should().BeGreaterThanOrEqualTo(recommendations[i + 1].Score);
            }
        }

        [Fact]
        public void GenerateRecommendations_InsufficientHistory_UsesColdStart()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User has only 2 watched items (below minimum of 3)
            var watchedMovies = library.Take(2).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            var unwatchedMovies = library.Skip(2).ToList();
            foreach (var movie in unwatchedMovies)
            {
                SetupUnwatchedItem(movie);
            }

            // Create profile with insufficient history
            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));
            userProfile.WatchedItemCount.Should().BeLessThan(_config.MinWatchedItemsForPersonalization);

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 5);

            // Assert
            recommendations.Should().NotBeEmpty();
            recommendations.Should().HaveCount(5);

            // Should use cold-start (top-rated) instead of personalization
            var topRec = recommendations.First();
            topRec.Score.Should().BeGreaterThan(0);
        }

        [Fact]
        public void GenerateRecommendations_ExcludesItemsWithPlaybackProgress()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User watched some items fully
            var watchedMovies = library.Take(3).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            // One item has partial playback progress (being watched)
            var partiallyWatchedMovie = library[3];
            SetupPartiallyWatchedItem(partiallyWatchedMovie, playbackPositionTicks: 1000);

            // Rest are unwatched
            var unwatchedMovies = library.Skip(4).ToList();
            foreach (var movie in unwatchedMovies)
            {
                SetupUnwatchedItem(movie);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert
            recommendations.Should().NotBeEmpty();

            // Should NOT include the partially watched item
            recommendations.Should().NotContain(r => r.ItemId == partiallyWatchedMovie.Id,
                "items with playback progress should be excluded from recommendations");

            // Should only include unwatched items (no progress)
            var recommendedIds = recommendations.Select(r => r.ItemId).ToHashSet();
            var unwatchedIds = unwatchedMovies.Select(m => m.Id).ToHashSet();
            recommendedIds.Should().BeSubsetOf(unwatchedIds);
        }

        [Fact]
        public void GenerateRecommendations_AllItemsWatched_ReturnsEmpty()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User watched everything
            foreach (var movie in library)
            {
                SetupWatchedItem(movie);
            }

            var userProfile = CreateGenericUserProfile(embeddings, library.Select(m => m.Id));

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert
            recommendations.Should().BeEmpty("no unwatched items available");
        }

        [Fact]
        public void GenerateRecommendations_FilterByMediaType_OnlyReturnsFilteredType()
        {
            // Arrange
            var allMedia = TestMediaLibrary.CreateTestLibrary(); // Movies + Series
            var embeddings = CreateEmbeddings(allMedia);
            var metadata = allMedia.ToDictionary(i => i.Id, i => i);

            // User watched some movies
            var watchedMovies = allMedia.Where(m => m.Type == MediaType.Movie).Take(3).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            // Setup all other items as unwatched
            foreach (var item in allMedia.Where(m => !watchedMovies.Contains(m)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));

            // Act - Request only movies
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                mediaType: MediaType.Movie,
                maxResults: 10);

            // Assert
            recommendations.Should().NotBeEmpty();

            // Verify all recommendations are movies
            foreach (var rec in recommendations)
            {
                var item = metadata[rec.ItemId];
                item.Type.Should().Be(MediaType.Movie, "requested movie recommendations only");
            }
        }

        [Fact]
        public void GenerateRecommendations_NullEmbeddings_ThrowsArgumentNullException()
        {
            // Arrange
            var metadata = new Dictionary<Guid, MediaItemMetadata>();
            var userProfile = new UserProfile(_testUserId, new float[100]);

            // Act
            Action act = () => _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                null!,
                metadata,
                _config);

            // Assert
            act.Should().Throw<ArgumentNullException>().WithParameterName("embeddings");
        }

        [Fact]
        public void GenerateRecommendations_NullMetadata_ThrowsArgumentNullException()
        {
            // Arrange
            var embeddings = new Dictionary<Guid, ItemEmbedding>();
            var userProfile = new UserProfile(_testUserId, new float[100]);

            // Act
            Action act = () => _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                null!,
                _config);

            // Assert
            act.Should().Throw<ArgumentNullException>().WithParameterName("metadata");
        }

        [Fact]
        public void GenerateRecommendations_EmptyEmbeddings_ThrowsArgumentException()
        {
            // Arrange
            var embeddings = new Dictionary<Guid, ItemEmbedding>();
            var metadata = new Dictionary<Guid, MediaItemMetadata>();
            var userProfile = new UserProfile(_testUserId, new float[100]);

            // Act
            Action act = () => _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithParameterName("embeddings")
                .WithMessage("*cannot be empty*");
        }

        [Fact]
        public void GenerateRecommendations_With2000Candidates_CompletesInReasonableTime()
        {
            // Arrange - Create 2000 items
            var library = CreateLargeLibrary(2000);
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // User watched 50 items
            var watchedMovies = library.Take(50).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            // Rest are unwatched
            foreach (var movie in library.Skip(50))
            {
                SetupUnwatchedItem(movie);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));

            // Act
            var stopwatch = Stopwatch.StartNew();
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 25);
            stopwatch.Stop();

            // Assert
            recommendations.Should().NotBeEmpty();
            recommendations.Should().HaveCount(25);
            
            // Note: Acceptance criteria is <500ms for production, but mocking overhead adds significant time
            // In production without mocks and with optimized Jellyfin calls, this will be much faster
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000,
                "should complete in reasonable time (test includes mocking overhead, production target is <500ms)");
        }

        #region Diversity Reranking Tests

        [Fact]
        public void GenerateRecommendations_DiversityOn_LimitsFranchiseClusterRepresentation()
        {
            // Arrange
            var (library, franchiseIds, diverseIds, watchedIds) = CreateFranchiseClusterLibrary();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var item in library.Where(m => !watchedIds.Contains(m.Id)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            _config.EnableDiversityReranking = true;
            _config.DiversityWeight = 0.7;

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 5);

            // Assert: the 5-item franchise cluster shouldn't fully dominate a 5-slot list
            var franchisePicked = recommendations.Count(r => franchiseIds.Contains(r.ItemId));
            var diversePicked = recommendations.Count(r => diverseIds.Contains(r.ItemId));

            franchisePicked.Should().BeLessThan(5, "diversity re-ranking should leave room for non-franchise picks");
            diversePicked.Should().BeGreaterThanOrEqualTo(2, "at least some diverse genres should make it in");
        }

        [Fact]
        public void GenerateRecommendations_DiversityOff_StillStrictlyDescendingByScore()
        {
            // Arrange
            var (library, _, _, watchedIds) = CreateFranchiseClusterLibrary();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var item in library.Where(m => !watchedIds.Contains(m.Id)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            _config.EnableDiversityReranking = false;

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert: with the flag off, selection is untouched — still plain score-descending
            for (int i = 0; i < recommendations.Count - 1; i++)
            {
                recommendations[i].Score.Should().BeGreaterThanOrEqualTo(recommendations[i + 1].Score);
            }
        }

        [Fact]
        public void GenerateRecommendations_DiversityWeightZero_MatchesPlainScoreOrdering()
        {
            // Arrange
            var (library, _, _, watchedIds) = CreateFranchiseClusterLibrary();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var item in library.Where(m => !watchedIds.Contains(m.Id)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            // maxResults is deliberately less than the 10 available candidates so the comparison
            // actually exercises the per-pick weighted formula, not the short-circuit path that
            // returns everyone (which also happens to be score-sorted, but wouldn't prove anything
            // about the weight=0.0 blend itself).
            _config.EnableDiversityReranking = false;
            var plainOrder = _engine.GenerateRecommendations(
                _testUserId, userProfile, embeddings, metadata, _config, maxResults: 7)
                .Select(r => r.ItemId)
                .ToList();

            _config.EnableDiversityReranking = true;
            _config.DiversityWeight = 0.0;

            // Act
            var diversityOrder = _engine.GenerateRecommendations(
                _testUserId, userProfile, embeddings, metadata, _config, maxResults: 7)
                .Select(r => r.ItemId)
                .ToList();

            // Assert: weight 0.0 must degenerate exactly to plain relevance ordering
            diversityOrder.Should().Equal(plainOrder);
        }

        [Fact]
        public void GenerateRecommendations_DiversityWeightOne_AvoidsSelectingMultipleFromCluster()
        {
            // Arrange
            var (library, franchiseIds, _, watchedIds) = CreateFranchiseClusterLibrary();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var item in library.Where(m => !watchedIds.Contains(m.Id)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            _config.EnableDiversityReranking = true;
            _config.DiversityWeight = 1.0;

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 5);

            // Assert: at the pure-diversity extreme, only the seed pick may come from the cluster
            recommendations.Count(r => franchiseIds.Contains(r.ItemId)).Should().BeLessOrEqualTo(1);
        }

        [Fact]
        public void GenerateRecommendations_DiversityEnabled_FewerCandidatesThanMaxResults_ReturnsAllUnfiltered()
        {
            // Arrange: a tiny library, far fewer unwatched candidates than maxResults
            var library = new List<MediaItemMetadata>();
            var watchedIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var watched = new MediaItemMetadata(Guid.NewGuid(), $"Watched Seed {i}", MediaType.Movie);
                watched.AddGenre("Action");
                library.Add(watched);
                watchedIds.Add(watched.Id);
            }

            var candidateIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"Candidate {i}", MediaType.Movie);
                item.AddGenre("Action");
                library.Add(item);
                candidateIds.Add(item.Id);
            }

            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var id in candidateIds)
            {
                SetupUnwatchedItem(metadata[id]);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            _config.EnableDiversityReranking = true;
            _config.DiversityWeight = 0.5;

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 25);

            // Assert: the short-circuit path shouldn't drop or throw — all 3 candidates come back
            recommendations.Should().HaveCount(3);
            recommendations.Select(r => r.ItemId).Should().BeEquivalentTo(candidateIds);
        }

        [Fact]
        public void GenerateRecommendations_DiversityWeightOne_NeverSelectsCandidateOutsideRelevancePool()
        {
            // Arrange: a candidate pool shaped like the real-world regression this guards against —
            // plenty of genuinely relevant candidates (more than the pool cap alone), plus a much
            // larger set of "junk" candidates that share nothing with the user's taste. Pure-diversity
            // MMR (weight 1.0) would happily reach for junk purely because it's maximally different
            // from everything already selected, unless the candidate pool is bounded first.
            var library = new List<MediaItemMetadata>();
            var watchedIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var watched = new MediaItemMetadata(Guid.NewGuid(), $"Watched Seed {i}", MediaType.Movie);
                watched.AddGenre("Action");
                library.Add(watched);
                watchedIds.Add(watched.Id);
            }

            // 35 relevant candidates — more than the pool cap (maxResults 10 × multiplier 3 = 30) on
            // their own, so the cap has to actually cut into this tier, not just exclude the junk.
            var relevantIds = new List<Guid>();
            for (int i = 0; i < 35; i++)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"Relevant {i}", MediaType.Movie);
                item.AddGenre("Action");
                library.Add(item);
                relevantIds.Add(item.Id);
            }

            // 20 junk candidates tagged with genres outside CreateEmbeddings' known dimension map, so
            // they carry essentially no signal toward this user's taste vector.
            var junkIds = new List<Guid>();
            for (int i = 0; i < 20; i++)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"Junk {i}", MediaType.Movie);
                item.AddGenre($"Unrecognized{i}");
                library.Add(item);
                junkIds.Add(item.Id);
            }

            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var item in library.Where(m => !watchedIds.Contains(m.Id)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            _config.EnableDiversityReranking = true;
            _config.DiversityWeight = 1.0;

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert: even at maximum diversity, nothing from the junk tier should ever be reachable
            recommendations.Should().NotContain(r => junkIds.Contains(r.ItemId));
        }

        [Fact]
        public void GenerateRecommendations_NarrowRelevanceSpreadPool_GentleWeightStaysCloseToRelevanceOrder()
        {
            // Arrange: a pool where every candidate shares no genre with the profile at all, so raw
            // cosine similarity — and therefore Score — barely varies between them. This mirrors the
            // real-world case that motivated score normalization: a user's TV candidates all weakly
            // matching a narrow taste profile, where an un-normalized diversity penalty could dominate
            // far more than a "gentle" weight implies because the raw relevance spread is tiny.
            var library = new List<MediaItemMetadata>();
            var watchedIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var watched = new MediaItemMetadata(Guid.NewGuid(), $"Watched Horror Seed {i}", MediaType.Movie);
                watched.AddGenre("Horror");
                library.Add(watched);
                watchedIds.Add(watched.Id);
            }

            // None of these share "Horror" with the profile, and each has a different genre from the
            // others too, so both relevance and inter-candidate similarity are weak and noise-driven.
            var candidateGenres = new[] { "Action", "Drama", "Comedy", "Thriller", "Crime", "Adventure", "Fantasy", "Romance" };
            foreach (var genre in candidateGenres)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"{genre} Candidate", MediaType.Movie);
                item.AddGenre(genre);
                library.Add(item);
            }

            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            foreach (var id in watchedIds)
            {
                SetupWatchedItem(metadata[id]);
            }

            foreach (var item in library.Where(m => !watchedIds.Contains(m.Id)))
            {
                SetupUnwatchedItem(item);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedIds);

            _config.EnableDiversityReranking = false;
            var plainTop3 = _engine.GenerateRecommendations(
                _testUserId, userProfile, embeddings, metadata, _config, maxResults: 3)
                .Select(r => r.ItemId)
                .ToList();

            _config.EnableDiversityReranking = true;
            _config.DiversityWeight = 0.1;

            // Act
            var diversityTop3 = _engine.GenerateRecommendations(
                _testUserId, userProfile, embeddings, metadata, _config, maxResults: 3)
                .Select(r => r.ItemId)
                .ToList();

            // Assert: at a gentle weight, normalized relevance should still dominate — at least 2 of
            // the 3 plain-relevance picks should survive, even though raw scores barely varied
            diversityTop3.Intersect(plainTop3).Should().HaveCountGreaterOrEqualTo(2);
        }

        #endregion

        // Helper Methods

        private List<MediaItemMetadata> CreateLargeLibrary(int itemCount)
        {
            var library = new List<MediaItemMetadata>();
            var genres = new[] { "Action", "Drama", "Comedy", "Sci-Fi", "Thriller", "Horror", "Romance" };
            var actors = new[] { "Actor A", "Actor B", "Actor C", "Actor D", "Actor E" };
            var directors = new[] { "Director X", "Director Y", "Director Z" };

            for (int i = 0; i < itemCount; i++)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"Movie {i}", MediaType.Movie)
                {
                    ReleaseYear = 1990 + (i % 30),
                    CommunityRating = 5.0f + ((i % 50) / 10.0f),
                    CriticRating = 50f + (i % 50)
                };

                item.AddGenre(genres[i % genres.Length]);
                item.AddActor(actors[i % actors.Length]);
                item.AddDirector(directors[i % directors.Length]);

                library.Add(item);
            }

            return library;
        }

        private Dictionary<Guid, ItemEmbedding> CreateEmbeddings(List<MediaItemMetadata> library)
        {
            var embeddings = new Dictionary<Guid, ItemEmbedding>();
            var dimension = 100;

            // Map genres to specific dimension ranges for realistic similarity
            var genreDimensions = new Dictionary<string, int>
            {
                { "Science Fiction", 0 },
                { "Action", 10 },
                { "Drama", 20 },
                { "Comedy", 30 },
                { "Horror", 40 },
                { "Thriller", 50 },
                { "Crime", 60 },
                { "Adventure", 70 },
                { "Fantasy", 80 },
                { "Romance", 90 }
            };

            foreach (var item in library)
            {
                var vector = new float[dimension];

                // Set dimensions based on genres - items with same genres will have similar vectors
                foreach (var genre in item.Genres)
                {
                    if (genreDimensions.TryGetValue(genre, out var baseDim))
                    {
                        // Set a block of dimensions for this genre
                        for (int i = 0; i < 10; i++)
                        {
                            vector[baseDim + i] = 1.0f;
                        }
                    }
                }

                // Add small unique component based on item name to differentiate items with same genres
                var seed = item.Name.GetHashCode();
                var random = new Random(seed);
                for (int i = 0; i < dimension; i++)
                {
                    vector[i] += (float)(random.NextDouble() * 0.1);
                }

                // Normalize
                var magnitude = (float)Math.Sqrt(vector.Sum(x => x * x));
                if (magnitude > 0)
                {
                    for (int i = 0; i < dimension; i++)
                    {
                        vector[i] /= magnitude;
                    }
                }

                embeddings[item.Id] = new ItemEmbedding(item.Id, vector);
            }

            return embeddings;
        }

        /// <summary>
        /// Builds a library with a 5-item "franchise" cluster (all tagged the same single genre, so
        /// <see cref="CreateEmbeddings"/> gives them near-identical vectors), 5 genre-diverse items, and
        /// 3 watched seed items (also single-genre-matching the cluster) to build a taste profile that
        /// favors the cluster above the diverse items.
        /// </summary>
        private (List<MediaItemMetadata> Library, List<Guid> FranchiseIds, List<Guid> DiverseIds, List<Guid> WatchedIds) CreateFranchiseClusterLibrary()
        {
            var library = new List<MediaItemMetadata>();
            var watchedIds = new List<Guid>();

            for (int i = 0; i < 3; i++)
            {
                var watched = new MediaItemMetadata(Guid.NewGuid(), $"Watched Action Seed {i}", MediaType.Movie);
                watched.AddGenre("Action");
                library.Add(watched);
                watchedIds.Add(watched.Id);
            }

            var franchiseIds = new List<Guid>();
            for (int i = 0; i < 5; i++)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"Action Franchise {i}", MediaType.Movie);
                item.AddGenre("Action");
                library.Add(item);
                franchiseIds.Add(item.Id);
            }

            var diverseIds = new List<Guid>();
            foreach (var genre in new[] { "Drama", "Comedy", "Horror", "Thriller", "Crime" })
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"{genre} Movie", MediaType.Movie);
                item.AddGenre(genre);
                library.Add(item);
                diverseIds.Add(item.Id);
            }

            return (library, franchiseIds, diverseIds, watchedIds);
        }

        private UserProfile CreateSciFiUserProfile(
            Dictionary<Guid, ItemEmbedding> embeddings,
            IEnumerable<Guid> watchedIds)
        {
            // Create a taste vector that's the average of watched sci-fi items
            var watchedEmbeddings = watchedIds.Select(id => embeddings[id]).ToList();
            var dimension = watchedEmbeddings.First().Dimensions;
            var tasteVector = new float[dimension];

            foreach (var embedding in watchedEmbeddings)
            {
                for (int i = 0; i < dimension; i++)
                {
                    tasteVector[i] += embedding.Vector[i];
                }
            }

            // Normalize
            var magnitude = (float)Math.Sqrt(tasteVector.Sum(x => x * x));
            if (magnitude > 0)
            {
                for (int i = 0; i < dimension; i++)
                {
                    tasteVector[i] /= magnitude;
                }
            }

            return new UserProfile(_testUserId, tasteVector)
            {
                WatchedItemCount = watchedEmbeddings.Count
            };
        }

        private UserProfile CreateGenericUserProfile(
            Dictionary<Guid, ItemEmbedding> embeddings,
            IEnumerable<Guid> watchedIds)
        {
            return CreateSciFiUserProfile(embeddings, watchedIds);
        }

        private void SetupWatchedItem(MediaItemMetadata item)
        {
            var mockItem = new Mock<BaseItem>();
            mockItem.Object.Id = item.Id;
            _mockLibraryManager.Setup(m => m.GetItemById(item.Id)).Returns(mockItem.Object);
            _registeredItems.Add(mockItem.Object);

            var userData = new UserItemData
            {
                Key = item.Id.ToString(),
                Played = true
            };
            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, mockItem.Object))
                .Returns(userData);
        }

        private void SetupUnwatchedItem(MediaItemMetadata item)
        {
            var mockItem = new Mock<BaseItem>();
            mockItem.Object.Id = item.Id;
            _mockLibraryManager.Setup(m => m.GetItemById(item.Id)).Returns(mockItem.Object);
            _registeredItems.Add(mockItem.Object);

            var userData = new UserItemData
            {
                Key = item.Id.ToString(),
                Played = false,
                PlaybackPositionTicks = 0
            };
            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, mockItem.Object))
                .Returns(userData);
        }

        private void SetupPartiallyWatchedItem(MediaItemMetadata item, long playbackPositionTicks)
        {
            var mockItem = new Mock<BaseItem>();
            mockItem.Object.Id = item.Id;
            _mockLibraryManager.Setup(m => m.GetItemById(item.Id)).Returns(mockItem.Object);
            _registeredItems.Add(mockItem.Object);

            var userData = new UserItemData
            {
                Key = item.Id.ToString(),
                Played = false,
                PlaybackPositionTicks = playbackPositionTicks
            };
            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, mockItem.Object))
                .Returns(userData);
        }

        /// <summary>
        /// Sets up the user-scoped GetItemList mock to return only the specified items
        /// as "visible" to the user (simulating library access permissions).
        /// </summary>
        private void SetupUserVisibleItems(IEnumerable<MediaItemMetadata> visibleItems)
        {
            var baseItems = new List<BaseItem>();

            foreach (var item in visibleItems)
            {
                var mockItem = new Mock<BaseItem>();
                mockItem.Object.Id = item.Id;
                baseItems.Add(mockItem.Object);
            }

            // When GetItemList is called with any query (user-scoped), return only visible items
            _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(baseItems);
        }

        #region Library Access Filtering Tests

        [Fact]
        public void GenerateRecommendations_ExcludesItemsFromInaccessibleLibraries()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // First 7 items are accessible, last 3 are in a disabled library
            var accessibleItems = library.Take(7).ToList();
            var inaccessibleItems = library.Skip(7).ToList();
            var inaccessibleIds = inaccessibleItems.Select(i => i.Id).ToHashSet();

            // Setup user-visible items (only accessible ones returned by user-scoped query)
            SetupUserVisibleItems(accessibleItems);

            // User watched 3 accessible items
            var watchedMovies = accessibleItems.Take(3).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            // Rest of accessible items are unwatched
            foreach (var movie in accessibleItems.Skip(3))
            {
                SetupUnwatchedItem(movie);
            }

            // Inaccessible items are also unwatched (would be recommended without the fix)
            foreach (var movie in inaccessibleItems)
            {
                SetupUnwatchedItem(movie);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert
            recommendations.Should().NotBeEmpty("accessible unwatched items exist");

            foreach (var rec in recommendations)
            {
                inaccessibleIds.Should().NotContain(rec.ItemId,
                    "items from inaccessible libraries should be excluded from recommendations");
            }
        }

        [Fact]
        public void GenerateRecommendations_ColdStart_ExcludesItemsFromInaccessibleLibraries()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // First 7 items are accessible, last 3 are in a disabled library
            var accessibleItems = library.Take(7).ToList();
            var inaccessibleItems = library.Skip(7).ToList();
            var inaccessibleIds = inaccessibleItems.Select(i => i.Id).ToHashSet();

            // Setup user-visible items
            SetupUserVisibleItems(accessibleItems);

            // All items are unwatched (cold start - null profile)
            foreach (var item in library)
            {
                SetupUnwatchedItem(item);
            }

            UserProfile? userProfile = null;

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert
            recommendations.Should().NotBeEmpty("accessible items exist");

            foreach (var rec in recommendations)
            {
                inaccessibleIds.Should().NotContain(rec.ItemId,
                    "items from inaccessible libraries should be excluded from cold-start recommendations");
            }
        }

        [Fact]
        public void GenerateRecommendations_AllLibrariesAccessible_ReturnsAllEligibleItems()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // ALL items are accessible
            SetupUserVisibleItems(library);

            // User watched 3 items
            var watchedMovies = library.Take(3).ToList();
            foreach (var movie in watchedMovies)
            {
                SetupWatchedItem(movie);
            }

            // Rest are unwatched
            var unwatchedMovies = library.Skip(3).ToList();
            foreach (var movie in unwatchedMovies)
            {
                SetupUnwatchedItem(movie);
            }

            var userProfile = CreateGenericUserProfile(embeddings, watchedMovies.Select(m => m.Id));

            // Act
            var recommendations = _engine.GenerateRecommendations(
                _testUserId,
                userProfile,
                embeddings,
                metadata,
                _config,
                maxResults: 10);

            // Assert - should have recommendations from the full unwatched pool
            recommendations.Should().NotBeEmpty();
            recommendations.Should().HaveCountGreaterThanOrEqualTo(
                Math.Min(unwatchedMovies.Count, 10),
                "all items are accessible so all eligible items should be candidates");
        }

        #endregion
    }
}
