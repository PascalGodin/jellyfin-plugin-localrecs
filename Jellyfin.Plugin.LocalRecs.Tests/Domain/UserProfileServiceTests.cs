using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using Jellyfin.Plugin.LocalRecs.Tests.Fixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Domain
{
    /// <summary>
    /// Tests for <see cref="UserProfileService"/>.
    /// Validates user profile building, weighting calculations, and edge case handling.
    /// </summary>
    public class UserProfileServiceTests
    {
        private readonly Mock<IUserDataManager> _mockUserDataManager;
        private readonly Mock<IUserManager> _mockUserManager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;
        private readonly UserProfileService _service;
        private readonly PluginConfiguration _config;
        private readonly Guid _testUserId;
        private readonly User _testUser;
        private readonly List<BaseItem> _allPlayedEpisodes;

        public UserProfileServiceTests()
        {
            _mockUserDataManager = new Mock<IUserDataManager>();
            _mockUserManager = new Mock<IUserManager>();
            _mockLibraryManager = new Mock<ILibraryManager>();
            _service = new UserProfileService(
                _mockUserDataManager.Object,
                _mockUserManager.Object,
                _mockLibraryManager.Object,
                NullLogger<UserProfileService>.Instance);

            _testUserId = Guid.NewGuid();
            _testUser = new User("TestUser", "Default", "Default");

            _config = new PluginConfiguration
            {
                FavoriteBoost = 2.0,
                RecencyDecayHalfLifeDays = 365.0,
                MinWatchedItemsForPersonalization = 3
            };

            // Setup user manager to return test user
            _mockUserManager.Setup(m => m.GetUserById(_testUserId)).Returns(_testUser);

            // Default mock for the bulk played-episodes query used by BuildSeriesLastPlayedMap.
            // Tests populate _allPlayedEpisodes via SetupSeriesWithWatchedEpisode before calling BuildUserProfile.
            _allPlayedEpisodes = new List<BaseItem>();
            _mockLibraryManager
                .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(() => _allPlayedEpisodes);
        }

        [Fact]
        public void BuildUserProfile_WithValidHistory_CreatesProfile()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            SetupUserDataMocks(library.Take(3).ToList(), daysAgo: 30);

            // Act
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            profile.Should().NotBeNull();
            profile!.UserId.Should().Be(_testUserId);
            profile.TasteVector.Should().NotBeEmpty();
            profile.WatchedItemCount.Should().Be(3);
            profile.TasteVector.Length.Should().Be(embeddings.Values.First().Dimensions);
        }

        [Fact]
        public void BuildUserProfile_TasteVector_IsNormalized()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            SetupUserDataMocks(library.Take(5).ToList(), daysAgo: 30);

            // Act
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            profile.Should().NotBeNull();
            var magnitude = Math.Sqrt(profile!.TasteVector.Sum(x => x * x));
            magnitude.Should().BeApproximately(1.0, 0.001, "taste vector should be normalized to unit length");
        }

        [Fact]
        public void BuildUserProfile_WithFavorites_FavoritesGetHigherWeight()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var matrix = library.First(m => m.Name == "The Matrix");
            var inception = library.First(m => m.Name == "Inception");

            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // Setup: Matrix is favorite, Inception is not (same recency)
            SetupSpecificUserData(matrix, isFavorite: true, daysAgo: 7);
            SetupSpecificUserData(inception, isFavorite: false, daysAgo: 7);

            // Act
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            profile.Should().NotBeNull();

            // Verify that the taste vector is more similar to the favorite item
            var matrixSimilarity = Utilities.VectorMath.CosineSimilarity(
                profile!.TasteVector,
                embeddings[matrix.Id].Vector);
            var inceptionSimilarity = Utilities.VectorMath.CosineSimilarity(
                profile.TasteVector,
                embeddings[inception.Id].Vector);

            // Matrix (favorite) should have higher influence on taste vector
            matrixSimilarity.Should().BeGreaterThan(inceptionSimilarity,
                "favorite item should have more influence due to favorite boost of {0}",
                _config.FavoriteBoost);
        }

        [Fact]
        public void BuildUserProfile_WithRecencyDecay_RecentWatchesWeightedHigher()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var recent = library.First(m => m.Name == "The Matrix");
            var old = library.First(m => m.Name == "Alien");

            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // Setup: Recent watch (7 days ago) vs old watch (at half-life = 365 days)
            SetupSpecificUserData(recent, isFavorite: false, daysAgo: 7);
            SetupSpecificUserData(old, isFavorite: false, daysAgo: 365);

            // Act
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            profile.Should().NotBeNull();

            // Verify that the taste vector is more similar to the recent item
            var recentSimilarity = Utilities.VectorMath.CosineSimilarity(
                profile!.TasteVector,
                embeddings[recent.Id].Vector);
            var oldSimilarity = Utilities.VectorMath.CosineSimilarity(
                profile.TasteVector,
                embeddings[old.Id].Vector);

            // Recent watch should have higher influence due to recency decay
            // At 365 days (half-life), weight is 0.5x, so recent should dominate
            recentSimilarity.Should().BeGreaterThan(oldSimilarity,
                "recent watch should have more influence due to recency decay with half-life of {0} days",
                _config.RecencyDecayHalfLifeDays);
        }

        [Fact]
        public void BuildUserProfile_NoWatchHistory_ReturnsNull()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // No user data setup - empty watch history

            // Act
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            profile.Should().BeNull();
        }

        [Fact]
        public void BuildUserProfile_NullEmbeddings_ThrowsArgumentNullException()
        {
            // Arrange
            var metadata = new Dictionary<Guid, MediaItemMetadata>();

            // Act
            Action act = () => _service.BuildUserProfile(_testUserId, null!, _config);

            // Assert
            act.Should().Throw<ArgumentNullException>().WithParameterName("embeddings");
        }

        [Fact]
        public void BuildUserProfile_NullConfig_ThrowsArgumentNullException()
        {
            // Arrange
            var embeddings = new Dictionary<Guid, ItemEmbedding>();

            // Act
            Action act = () => _service.BuildUserProfile(_testUserId, embeddings, null!);

            // Assert
            act.Should().Throw<ArgumentNullException>().WithParameterName("config");
        }

        [Fact]
        public void BuildUserProfile_EmptyEmbeddings_ThrowsArgumentException()
        {
            // Arrange
            var embeddings = new Dictionary<Guid, ItemEmbedding>();

            // Act
            Action act = () => _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            act.Should().Throw<ArgumentException>()
                .WithParameterName("embeddings")
                .WithMessage("Embeddings dictionary cannot be empty*");
        }

        [Fact]
        public void BuildUserProfile_UserNotFound_ReturnsNull()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            var unknownUserId = Guid.NewGuid();

            // Setup: Return null for unknown user
            _mockUserManager.Setup(m => m.GetUserById(unknownUserId)).Returns((User?)null);

            // Act
            var (profile, _) = _service.BuildUserProfile(unknownUserId, embeddings, _config);

            // Assert
            profile.Should().BeNull("user not found means no watch history");
        }

        [Fact]
        public void BuildUserProfile_IgnoresUnplayedItems()
        {
            // Arrange
            var library = TestMediaLibrary.CreateTestMovies();
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);

            // Setup 3 played items
            var playedItems = library.Take(3).ToList();
            SetupUserDataMocks(playedItems, daysAgo: 30);

            // Setup 2 unplayed items
            foreach (var item in library.Skip(3).Take(2))
            {
                var mockItem = new Mock<BaseItem>();
                _mockLibraryManager.Setup(m => m.GetItemById(item.Id)).Returns(mockItem.Object);

                var userData = new UserItemData
                {
                    Key = item.Id.ToString(),
                    Played = false // Not played
                };
                _mockUserDataManager.Setup(m => m.GetUserData(_testUser, mockItem.Object))
                    .Returns(userData);
            }

            // Act
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            // Assert
            profile.Should().NotBeNull();
            profile!.WatchedItemCount.Should().Be(3, "only played items should be counted");
        }

        [Fact]
        public void BuildUserProfile_With100WatchedItems_CompletesUnder100ms()
        {
            // Arrange - create 100 items
            var library = CreateLargeLibrary(100);
            var embeddings = CreateEmbeddings(library);
            var metadata = library.ToDictionary(i => i.Id, i => i);
            SetupUserDataMocks(library, daysAgo: 30);

            // Act
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);
            stopwatch.Stop();

            // Assert
            profile.Should().NotBeNull();
            profile!.WatchedItemCount.Should().Be(100);
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(100,
                "acceptance criteria requires <100ms for 100 watched items");
        }

        [Fact]
        public void BuildUserProfile_Series_UsesMostRecentWatchedEpisodeDateForRecency()
        {
            // Regression test: Jellyfin never sets LastPlayedDate on the series-level UserItemData,
            // so the old code fell back to "today" for every series, giving them all maximum weight.
            // Series recency must come from the most recently watched episode.
            var recentMovie = new MediaItemMetadata(Guid.NewGuid(), "Recent Movie", MediaType.Movie);
            var oldSeries = new MediaItemMetadata(Guid.NewGuid(), "Old Series", MediaType.Series);
            var library = new List<MediaItemMetadata> { recentMovie, oldSeries };
            var embeddings = CreateEmbeddings(library);

            SetupSpecificUserData(recentMovie, isFavorite: false, daysAgo: 7);
            SetupSeriesWithWatchedEpisode(oldSeries, episodeDaysAgo: 400, isFavorite: false);

            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            profile.Should().NotBeNull();
            profile!.WatchedItemCount.Should().Be(2);

            var movieSimilarity = Utilities.VectorMath.CosineSimilarity(
                profile.TasteVector, embeddings[recentMovie.Id].Vector);
            var seriesSimilarity = Utilities.VectorMath.CosineSimilarity(
                profile.TasteVector, embeddings[oldSeries.Id].Vector);

            movieSimilarity.Should().BeGreaterThan(seriesSimilarity,
                "a movie watched 7 days ago should outweigh a series whose most recent episode " +
                "was watched 400 days ago");
        }

        [Fact]
        public void BuildUserProfile_Series_WithNoWatchedEpisodes_IsExcluded()
        {
            var watchedSeries = new MediaItemMetadata(Guid.NewGuid(), "Watched Series", MediaType.Series);
            var unwatchedSeries = new MediaItemMetadata(Guid.NewGuid(), "Unwatched Series", MediaType.Series);
            var library = new List<MediaItemMetadata> { watchedSeries, unwatchedSeries };
            var embeddings = CreateEmbeddings(library);

            SetupSeriesWithWatchedEpisode(watchedSeries, episodeDaysAgo: 30, isFavorite: false);
            SetupSeriesWithNoWatchedEpisodes(unwatchedSeries);

            var (profile, _) = _service.BuildUserProfile(_testUserId, embeddings, _config);

            profile.Should().NotBeNull();
            profile!.WatchedItemCount.Should().Be(1, "only the series with a watched episode should count");
        }

        // Helper methods

        private List<MediaItemMetadata> CreateLargeLibrary(int itemCount)
        {
            var library = new List<MediaItemMetadata>();
            var genres = new[] { "Action", "Drama", "Comedy", "Sci-Fi", "Thriller" };
            var actors = new[] { "Actor A", "Actor B", "Actor C", "Actor D" };
            var directors = new[] { "Director X", "Director Y", "Director Z" };

            for (int i = 0; i < itemCount; i++)
            {
                var item = new MediaItemMetadata(Guid.NewGuid(), $"Movie {i}", MediaType.Movie)
                {
                    ReleaseYear = 1990 + (i % 30),
                    CommunityRating = 5.0f + (i % 5),
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
            var dimension = 100; // Test dimension

            foreach (var item in library)
            {
                var vector = new float[dimension];
                var stableHash = 0;
                unchecked
                {
                    foreach (var c in item.Name) stableHash = (stableHash * 31) + c;
                }

                for (int i = 0; i < dimension; i++)
                {
                    vector[i] = (float)Math.Sin(i + (stableHash % 100));
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

        private void SetupUserDataMocks(List<MediaItemMetadata> watchedItems, int daysAgo)
        {
            foreach (var item in watchedItems)
            {
                SetupSpecificUserData(item, isFavorite: false, daysAgo: daysAgo);
            }
        }

        private void SetupSpecificUserData(
            MediaItemMetadata item,
            bool isFavorite,
            int daysAgo)
        {
            var mockItem = new Mock<BaseItem>();
            _mockLibraryManager.Setup(m => m.GetItemById(item.Id)).Returns(mockItem.Object);

            var userData = new UserItemData
            {
                Key = item.Id.ToString(),
                Played = true,
                IsFavorite = isFavorite,
                LastPlayedDate = DateTime.UtcNow.AddDays(-daysAgo)
            };

            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, mockItem.Object))
                .Returns(userData);
        }

        private void SetupSeriesWithWatchedEpisode(
            MediaItemMetadata seriesMeta,
            int episodeDaysAgo,
            bool isFavorite)
        {
            var series = new Series { Id = seriesMeta.Id, Name = seriesMeta.Name };
            _mockLibraryManager.Setup(m => m.GetItemById(seriesMeta.Id)).Returns(series);

            // Series-level user data has no LastPlayedDate, mirroring real Jellyfin behaviour.
            var seriesUserData = new UserItemData
            {
                Key = seriesMeta.Id.ToString(),
                IsFavorite = isFavorite,
                Played = false
            };
            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, series)).Returns(seriesUserData);

            // Add the episode to the shared list returned by the bulk GetItemList query.
            // SeriesId links it back to its parent series so BuildSeriesLastPlayedMap can group by series.
            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = seriesMeta.Name + " S01E01",
                SeriesId = seriesMeta.Id
            };
            _allPlayedEpisodes.Add(episode);

            var episodeUserData = new UserItemData
            {
                Key = episode.Id.ToString(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-episodeDaysAgo)
            };
            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, episode)).Returns(episodeUserData);
        }

        private void SetupSeriesWithNoWatchedEpisodes(MediaItemMetadata seriesMeta)
        {
            var series = new Series { Id = seriesMeta.Id, Name = seriesMeta.Name };
            _mockLibraryManager.Setup(m => m.GetItemById(seriesMeta.Id)).Returns(series);

            var seriesUserData = new UserItemData
            {
                Key = seriesMeta.Id.ToString(),
                Played = false
            };
            _mockUserDataManager.Setup(m => m.GetUserData(_testUser, series)).Returns(seriesUserData);

            // No episode added to _allPlayedEpisodes — TryGetValue returns false and the series is excluded.
        }
    }
}
