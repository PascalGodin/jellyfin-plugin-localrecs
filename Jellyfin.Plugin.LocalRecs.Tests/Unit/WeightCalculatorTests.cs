using System;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit
{
    public class WeightCalculatorTests
    {
        [Fact]
        public void ExponentialDecay_ZeroDays_ReturnsOne()
        {
            var result = WeightCalculator.ExponentialDecay(daysSince: 0, halfLifeDays: 365);

            result.Should().BeApproximately(1.0f, 0.0001f);
        }

        [Fact]
        public void ExponentialDecay_AtHalfLife_ReturnsHalf()
        {
            var result = WeightCalculator.ExponentialDecay(daysSince: 365, halfLifeDays: 365);

            result.Should().BeApproximately(0.5f, 0.0001f);
        }

        [Fact]
        public void ExponentialDecay_TwoHalfLives_ReturnsQuarter()
        {
            var result = WeightCalculator.ExponentialDecay(daysSince: 730, halfLifeDays: 365);

            result.Should().BeApproximately(0.25f, 0.0001f);
        }

        [Fact]
        public void ExponentialDecay_NegativeDays_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ExponentialDecay(daysSince: -1, halfLifeDays: 365);

            act.Should().Throw<ArgumentException>().WithParameterName("daysSince");
        }

        [Fact]
        public void ExponentialDecay_ZeroHalfLife_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ExponentialDecay(daysSince: 10, halfLifeDays: 0);

            act.Should().Throw<ArgumentException>().WithParameterName("halfLifeDays");
        }

        [Fact]
        public void ExponentialDecay_NegativeHalfLife_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ExponentialDecay(daysSince: 10, halfLifeDays: -365);

            act.Should().Throw<ArgumentException>().WithParameterName("halfLifeDays");
        }

        [Fact]
        public void ApplyFavoriteBoost_IsFavorite_AppliesBoost()
        {
            var result = WeightCalculator.ApplyFavoriteBoost(baseWeight: 1.0f, isFavorite: true, favoriteBoost: 2.0f);

            result.Should().Be(2.0f);
        }

        [Fact]
        public void ApplyFavoriteBoost_NotFavorite_ReturnsBaseWeight()
        {
            var result = WeightCalculator.ApplyFavoriteBoost(baseWeight: 1.0f, isFavorite: false, favoriteBoost: 2.0f);

            result.Should().Be(1.0f);
        }

        [Fact]
        public void ApplyFavoriteBoost_NegativeBaseWeight_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ApplyFavoriteBoost(baseWeight: -1.0f, isFavorite: true, favoriteBoost: 2.0f);

            act.Should().Throw<ArgumentException>().WithParameterName("baseWeight");
        }

        [Fact]
        public void ApplyFavoriteBoost_NegativeFavoriteBoost_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ApplyFavoriteBoost(baseWeight: 1.0f, isFavorite: true, favoriteBoost: -2.0f);

            act.Should().Throw<ArgumentException>().WithParameterName("favoriteBoost");
        }

        [Fact]
        public void ApplyRecentWatchBoost_ZeroBoost_ReturnsDecay()
        {
            var result = WeightCalculator.ApplyRecentWatchBoost(decay: 0.5f, recentWatchBoost: 0.0f);

            result.Should().BeApproximately(0.5f, 0.0001f);
        }

        [Fact]
        public void ApplyRecentWatchBoost_JustWatchedWithBoost_DoublesWeight()
        {
            // decay=1.0 (watched today), recentWatchBoost=1.0 → 1.0 × (1 + 1.0 × 1.0) = 2.0
            var result = WeightCalculator.ApplyRecentWatchBoost(decay: 1.0f, recentWatchBoost: 1.0f);

            result.Should().BeApproximately(2.0f, 0.0001f);
        }

        [Fact]
        public void ApplyRecentWatchBoost_OldItemsLessAmplified()
        {
            // Recent item (decay=1.0) gets 2× with boost=1; older item (decay=0.5) only 1.25×
            var recentWeight = WeightCalculator.ApplyRecentWatchBoost(decay: 1.0f, recentWatchBoost: 1.0f);
            var oldWeight = WeightCalculator.ApplyRecentWatchBoost(decay: 0.5f, recentWatchBoost: 1.0f);

            var recentRatio = recentWeight / 1.0f; // vs no-boost baseline
            var oldRatio = oldWeight / 0.5f;

            recentRatio.Should().BeGreaterThan(oldRatio, "anchor boost amplifies recent items more than old ones");
        }

        [Fact]
        public void ApplyRecentWatchBoost_NegativeDecay_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ApplyRecentWatchBoost(decay: -0.1f, recentWatchBoost: 1.0f);

            act.Should().Throw<ArgumentException>().WithParameterName("decay");
        }

        [Fact]
        public void ApplyRecentWatchBoost_DecayAboveOne_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ApplyRecentWatchBoost(decay: 1.1f, recentWatchBoost: 1.0f);

            act.Should().Throw<ArgumentException>().WithParameterName("decay");
        }

        [Fact]
        public void ApplyRecentWatchBoost_NegativeAnchorBoost_ThrowsArgumentException()
        {
            Action act = () => WeightCalculator.ApplyRecentWatchBoost(decay: 0.5f, recentWatchBoost: -1.0f);

            act.Should().Throw<ArgumentException>().WithParameterName("recentWatchBoost");
        }

        [Fact]
        public void ComputeCombinedWeight_FavoriteItem_AppliesDecayAndBoost()
        {
            // recentWatchBoost=0: weight = decay × (1 + 0) × favoriteBoost = decay × favoriteBoost
            // decay=0.5, favorite=0.5×2=1.0
            var result = WeightCalculator.ComputeCombinedWeight(
                daysSince: 365,
                halfLifeDays: 365,
                isFavorite: true,
                favoriteBoost: 2.0f,
                recentWatchBoost: 0.0f);

            result.Should().BeApproximately(1.0f, 0.0001f);
        }

        [Fact]
        public void ComputeCombinedWeight_NonFavorite_AppliesOnlyDecay()
        {
            // recentWatchBoost=0: weight = decay × 1 = decay
            var result = WeightCalculator.ComputeCombinedWeight(
                daysSince: 365,
                halfLifeDays: 365,
                isFavorite: false,
                favoriteBoost: 2.0f,
                recentWatchBoost: 0.0f);

            result.Should().BeApproximately(0.5f, 0.0001f);
        }

        [Fact]
        public void ComputeCombinedWeight_WithAnchorBoost_AmplifiiesJustWatchedItems()
        {
            // decay=1.0 (watched today), recentWatchBoost=1.0 → 1.0 × (1 + 1.0) × 1 = 2.0
            var result = WeightCalculator.ComputeCombinedWeight(
                daysSince: 0,
                halfLifeDays: 365,
                isFavorite: false,
                favoriteBoost: 1.0f,
                recentWatchBoost: 1.0f);

            result.Should().BeApproximately(2.0f, 0.0001f);
        }

    }
}
