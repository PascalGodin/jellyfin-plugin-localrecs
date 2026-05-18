using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Domain
{
    /// <summary>
    /// Tests for <see cref="LeavingSoonService"/>.
    /// Validates the 4-pass pipeline: cleanup, promote, discover, sync.
    /// </summary>
    public sealed class LeavingSoonServiceTests : IDisposable
    {
        private readonly Mock<IUserDataManager> _mockUserDataManager;
        private readonly Mock<IUserManager> _mockUserManager;
        private readonly Mock<ILibraryManager> _mockLibraryManager;
        private readonly UserProfileService _userProfileService;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly string _tempDataPath;
        private readonly string _stateFilePath;
        private readonly PluginConfiguration _config;

        public LeavingSoonServiceTests()
        {
            _mockUserDataManager = new Mock<IUserDataManager>();
            _mockUserManager = new Mock<IUserManager>();
            _mockLibraryManager = new Mock<ILibraryManager>();

            _tempDataPath = Path.Combine(Path.GetTempPath(), $"LocalRecsTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDataPath);
            _stateFilePath = Path.Combine(_tempDataPath, "leaving-soon-state.json");

            _userProfileService = new UserProfileService(
                _mockUserDataManager.Object,
                _mockUserManager.Object,
                _mockLibraryManager.Object,
                NullLogger<UserProfileService>.Instance);

            _virtualLibraryManager = new VirtualLibraryManager(
                NullLogger<VirtualLibraryManager>.Instance,
                _mockLibraryManager.Object,
                Path.Combine(_tempDataPath, "virtual-libraries"));

            _config = new PluginConfiguration
            {
                LeavingSoonEnabled = true,
                LeavingSoonMovieCount = 5,
                LeavingSoonTvCount = 5,
                LeavingSoonMinAgeDays = 0,
                LeavingSoonDwellDays = 30,
                MinWatchedItemsForPersonalization = 3
            };

            _mockUserManager.Setup(m => m.Users).Returns(new List<User>());
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDataPath))
            {
                try
                {
                    Directory.Delete(_tempDataPath, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }

        #region Null Argument Validation

        [Fact]
        public void RefreshAsync_NullAllItems_ThrowsArgumentNullException()
        {
            var service = CreateService();
            Action act = () => service.RefreshAsync(null!, new Dictionary<Guid, ItemEmbedding>(), _config, CancellationToken.None);
            act.Should().Throw<ArgumentNullException>().WithParameterName("allItems");
        }

        [Fact]
        public void RefreshAsync_NullEmbeddings_ThrowsArgumentNullException()
        {
            var service = CreateService();
            Action act = () => service.RefreshAsync(new List<MediaItemMetadata>(), null!, _config, CancellationToken.None);
            act.Should().Throw<ArgumentNullException>().WithParameterName("embeddings");
        }

        [Fact]
        public void RefreshAsync_NullConfig_ThrowsArgumentNullException()
        {
            var service = CreateService();
            Action act = () => service.RefreshAsync(new List<MediaItemMetadata>(), new Dictionary<Guid, ItemEmbedding>(), null!, CancellationToken.None);
            act.Should().Throw<ArgumentNullException>().WithParameterName("config");
        }

        #endregion

        #region Cleanup Pass

        [Fact]
        public async Task RefreshAsync_Cleanup_RemovesDeletedItems()
        {
            var deletedId = Guid.NewGuid();
            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[deletedId.ToString()] = DateTime.UtcNow.AddDays(-1);
            WriteState(initialState);

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata>(),
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().NotContainKey(deletedId.ToString(), "item was deleted from library");
            result.RemovalCandidates.Should().BeEmpty();
        }

        [Fact]
        public async Task RefreshAsync_Cleanup_RemovesMetadataPoorItems()
        {
            var itemId = Guid.NewGuid();
            var meta = new MediaItemMetadata(itemId, "Empty Movie", MediaType.Movie);
            // No genres and no actors → metadata-poor

            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[itemId.ToString()] = DateTime.UtcNow.AddDays(-1);
            WriteState(initialState);

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { meta },
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().NotContainKey(itemId.ToString(), "item has no genres or actors");
        }

        [Fact]
        public async Task RefreshAsync_Cleanup_RemovesItemsPlayedByAnyUser()
        {
            var itemId = Guid.NewGuid();
            var meta = CreateMovieMeta(itemId);

            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[itemId.ToString()] = DateTime.UtcNow.AddDays(-1);
            WriteState(initialState);

            var user = new User("TestUser", "Default", "Default");
            _mockUserManager.Setup(m => m.Users).Returns(new List<User> { user });

            var mockItem = new Mock<BaseItem>();
            _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(mockItem.Object);
            _mockUserDataManager
                .Setup(m => m.GetUserData(user, mockItem.Object))
                .Returns(new UserItemData { Key = itemId.ToString(), Played = true });

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { meta },
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().NotContainKey(itemId.ToString(), "item was played by a user");
        }

        [Fact]
        public async Task RefreshAsync_Cleanup_RemovesItemsFavoritedByAnyUser()
        {
            var itemId = Guid.NewGuid();
            var meta = CreateMovieMeta(itemId);

            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[itemId.ToString()] = DateTime.UtcNow.AddDays(-1);
            WriteState(initialState);

            var user = new User("TestUser", "Default", "Default");
            _mockUserManager.Setup(m => m.Users).Returns(new List<User> { user });

            var mockItem = new Mock<BaseItem>();
            _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(mockItem.Object);
            _mockUserDataManager
                .Setup(m => m.GetUserData(user, mockItem.Object))
                .Returns(new UserItemData { Key = itemId.ToString(), IsFavorite = true });

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { meta },
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().NotContainKey(itemId.ToString(), "item was favorited by a user");
        }

        [Fact]
        public async Task RefreshAsync_Cleanup_CollectionProtection_RemovesFlaggedItemWhenSiblingIsSaved()
        {
            var flaggedId = Guid.NewGuid();
            var siblingId = Guid.NewGuid();
            const string collection = "Test Franchise";

            var flaggedMeta = CreateMovieMeta(flaggedId, collection);
            var siblingMeta = CreateMovieMeta(siblingId, collection);

            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[flaggedId.ToString()] = DateTime.UtcNow.AddDays(-1);
            WriteState(initialState);

            var user = new User("TestUser", "Default", "Default");
            _mockUserManager.Setup(m => m.Users).Returns(new List<User> { user });

            var mockFlagged = new Mock<BaseItem>();
            var mockSibling = new Mock<BaseItem>();
            _mockLibraryManager.Setup(m => m.GetItemById(flaggedId)).Returns(mockFlagged.Object);
            _mockLibraryManager.Setup(m => m.GetItemById(siblingId)).Returns(mockSibling.Object);

            _mockUserDataManager
                .Setup(m => m.GetUserData(user, mockFlagged.Object))
                .Returns(new UserItemData { Key = flaggedId.ToString(), Played = false });
            _mockUserDataManager
                .Setup(m => m.GetUserData(user, mockSibling.Object))
                .Returns(new UserItemData { Key = siblingId.ToString(), Played = true });

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { flaggedMeta, siblingMeta },
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().NotContainKey(flaggedId.ToString(),
                "the collection is safe because a sibling was played by a user");
        }

        #endregion

        #region Promote Pass

        [Fact]
        public async Task RefreshAsync_Promote_MovesOverdueFlaggedItemsToRemovalCandidates()
        {
            var itemId = Guid.NewGuid();
            var meta = CreateMovieMeta(itemId);

            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[itemId.ToString()] = DateTime.UtcNow.AddDays(-(_config.LeavingSoonDwellDays + 1));
            WriteState(initialState);

            // No users → cleanup "saved by any user" check always passes
            var mockItem = new Mock<BaseItem>();
            _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(mockItem.Object);

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { meta },
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().NotContainKey(itemId.ToString());
            result.RemovalCandidates.Should().ContainKey(itemId.ToString(),
                "item exceeded the dwell period and should be promoted");
        }

        [Fact]
        public async Task RefreshAsync_Promote_KeepsRecentFlaggedItems()
        {
            var itemId = Guid.NewGuid();
            var meta = CreateMovieMeta(itemId);

            var initialState = new LeavingSoonState();
            initialState.FlaggedItems[itemId.ToString()] = DateTime.UtcNow.AddDays(-1);
            WriteState(initialState);

            var mockItem = new Mock<BaseItem>();
            _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(mockItem.Object);

            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { meta },
                new Dictionary<Guid, ItemEmbedding>(),
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().ContainKey(itemId.ToString(),
                "item was flagged recently and has not exceeded the dwell period");
            result.RemovalCandidates.Should().NotContainKey(itemId.ToString());
        }

        #endregion

        #region Discovery Pass

        [Fact]
        public async Task RefreshAsync_Discovery_SkippedWhenNoEligibleUsers()
        {
            // With no users, eligible profiles list is empty and Discover is skipped
            var itemId = Guid.NewGuid();
            var meta = CreateMovieMeta(itemId);
            var embedding = new ItemEmbedding(itemId, new float[] { 1f, 0f, 0f });

            // _mockUserManager.Users already returns empty list (set in constructor)
            var service = CreateService();

            await service.RefreshAsync(
                new List<MediaItemMetadata> { meta },
                new Dictionary<Guid, ItemEmbedding> { { itemId, embedding } },
                _config,
                CancellationToken.None);

            var result = ReadState();
            result.FlaggedItems.Should().BeEmpty("discovery is skipped when no users have enough watch history");
            result.RemovalCandidates.Should().BeEmpty();
        }

        #endregion

        #region Helpers

        private LeavingSoonService CreateService() =>
            new LeavingSoonService(
                NullLogger<LeavingSoonService>.Instance,
                _mockUserManager.Object,
                _mockUserDataManager.Object,
                _mockLibraryManager.Object,
                _userProfileService,
                _virtualLibraryManager,
                _tempDataPath);

        private void WriteState(LeavingSoonState state)
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }

        private LeavingSoonState ReadState()
        {
            if (!File.Exists(_stateFilePath))
            {
                return new LeavingSoonState();
            }

            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<LeavingSoonState>(json) ?? new LeavingSoonState();
        }

        private static MediaItemMetadata CreateMovieMeta(Guid id, string? collectionName = null)
        {
            var meta = new MediaItemMetadata(id, $"Movie {id:N}", MediaType.Movie);
            meta.AddGenre("Drama");
            meta.CollectionName = collectionName;
            return meta;
        }

        #endregion
    }
}
