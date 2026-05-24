using System;

namespace Jellyfin.Plugin.LocalRecs.Utilities
{
    /// <summary>
    /// Pure functions for computing weights (recency decay, favorite boost, recent watch boost).
    /// </summary>
    public static class WeightCalculator
    {
        /// <summary>
        /// Computes exponential recency decay weight.
        /// Weight = 0.5 ^ (days_since / half_life).
        /// </summary>
        /// <param name="daysSince">Number of days since the event.</param>
        /// <param name="halfLifeDays">Half-life in days (time for weight to decay to 50%).</param>
        /// <returns>Decay weight (0 to 1).</returns>
        /// <exception cref="ArgumentException">Thrown when daysSince is negative or halfLifeDays is less than or equal to 0.</exception>
        public static float ExponentialDecay(double daysSince, double halfLifeDays)
        {
            if (daysSince < 0)
            {
                throw new ArgumentException("Days since cannot be negative", nameof(daysSince));
            }

            if (halfLifeDays <= 0)
            {
                throw new ArgumentException("Half-life must be greater than 0", nameof(halfLifeDays));
            }

            // Weight = 0.5 ^ (days_since / half_life)
            return (float)Math.Pow(0.5, daysSince / halfLifeDays);
        }

        /// <summary>
        /// Applies favorite boost multiplier.
        /// </summary>
        /// <param name="baseWeight">The base weight.</param>
        /// <param name="isFavorite">Whether the item is marked as favorite.</param>
        /// <param name="favoriteBoost">Multiplier for favorites.</param>
        /// <returns>Boosted weight.</returns>
        /// <exception cref="ArgumentException">Thrown when baseWeight or favoriteBoost is negative.</exception>
        public static float ApplyFavoriteBoost(float baseWeight, bool isFavorite, float favoriteBoost)
        {
            if (baseWeight < 0)
            {
                throw new ArgumentException("Base weight cannot be negative", nameof(baseWeight));
            }

            if (favoriteBoost < 0)
            {
                throw new ArgumentException("Favorite boost cannot be negative", nameof(favoriteBoost));
            }

            return isFavorite ? baseWeight * favoriteBoost : baseWeight;
        }

        /// <summary>
        /// Applies recent watch boost to amplify the contribution of recently watched items.
        /// Items watched recently (high decay) receive a proportionally larger boost than
        /// older items (low decay), making fresh engagement more prominent in the taste profile.
        /// Weight = decay × (1 + recentWatchBoost × decay).
        /// </summary>
        /// <param name="decay">Recency decay value (0 to 1).</param>
        /// <param name="recentWatchBoost">Anchor boost scalar (0 = no boost, 1 = doubles weight of just-watched items).</param>
        /// <returns>Boosted weight.</returns>
        /// <exception cref="ArgumentException">Thrown when decay is outside [0,1] or recentWatchBoost is negative.</exception>
        public static float ApplyRecentWatchBoost(float decay, float recentWatchBoost)
        {
            if (decay < 0 || decay > 1)
            {
                throw new ArgumentException("Decay must be between 0 and 1", nameof(decay));
            }

            if (recentWatchBoost < 0)
            {
                throw new ArgumentException("Anchor boost cannot be negative", nameof(recentWatchBoost));
            }

            return decay * (1.0f + (recentWatchBoost * decay));
        }

        /// <summary>
        /// Computes the combined weight for a watch record.
        /// Weight = anchor_boost(decay) × favorite_boost.
        /// Anchor boost amplifies recently watched items without relying on play count,
        /// which is unreliable in Jellyfin due to stop-start event inflation.
        /// </summary>
        /// <param name="daysSince">Days since last watched.</param>
        /// <param name="halfLifeDays">Recency decay half-life.</param>
        /// <param name="isFavorite">Whether the item is favorite.</param>
        /// <param name="favoriteBoost">Favorite boost multiplier.</param>
        /// <param name="recentWatchBoost">Recent recent watch boost scalar (0 = no boost).</param>
        /// <returns>Combined weight.</returns>
        public static float ComputeCombinedWeight(
            double daysSince,
            double halfLifeDays,
            bool isFavorite,
            float favoriteBoost,
            float recentWatchBoost)
        {
            float decay = ExponentialDecay(daysSince, halfLifeDays);
            float weight = ApplyRecentWatchBoost(decay, recentWatchBoost);
            weight = ApplyFavoriteBoost(weight, isFavorite, favoriteBoost);
            return weight;
        }
    }
}
