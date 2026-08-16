using System;

namespace Jellyfin.Plugin.LocalRecs.Utilities
{
    /// <summary>
    /// Shared scoring logic for blending content-vector similarity with rating proximity.
    /// Used by both the recommendation engine and Leaving Soon discovery so the two features
    /// agree on what counts as a taste match for a given profile/item pair.
    /// </summary>
    public static class TasteScoring
    {
        /// <summary>
        /// Computes the blended taste score between a taste vector (and its owner's average ratings)
        /// and a candidate item (vector plus ratings).
        /// </summary>
        /// <param name="tasteVector">The taste vector to compare against.</param>
        /// <param name="profileAverageCommunityRating">The taste vector owner's average community rating, or null if unavailable.</param>
        /// <param name="profileAverageCriticRating">The taste vector owner's average critic rating, or null if unavailable.</param>
        /// <param name="itemVector">The candidate item's embedding vector.</param>
        /// <param name="itemCommunityRating">The candidate item's community rating (0-10), or null if unavailable.</param>
        /// <param name="itemCriticRating">The candidate item's critic rating (0-100), or null if unavailable.</param>
        /// <param name="enableRatingProximity">Whether rating proximity blending is enabled.</param>
        /// <param name="ratingProximityWeight">Weight (0-1) given to rating proximity in the blend.</param>
        /// <returns>The computed <see cref="TasteScore"/>.</returns>
        public static TasteScore Compute(
            float[] tasteVector,
            float? profileAverageCommunityRating,
            float? profileAverageCriticRating,
            float[] itemVector,
            float? itemCommunityRating,
            float? itemCriticRating,
            bool enableRatingProximity,
            double ratingProximityWeight)
        {
            var cosineSimilarity = VectorMath.CosineSimilarity(tasteVector, itemVector);

            if (!enableRatingProximity)
            {
                return new TasteScore(cosineSimilarity, null, null, null, cosineSimilarity);
            }

            double communityProximity = 0.5; // neutral default
            double criticProximity = 0.5;    // neutral default

            if (itemCommunityRating.HasValue && profileAverageCommunityRating.HasValue)
            {
                var diff = Math.Abs(itemCommunityRating.Value - profileAverageCommunityRating.Value);

                // Community rating is 0-10 scale
                communityProximity = Math.Max(0, 1.0 - (diff / 10.0));
            }

            if (itemCriticRating.HasValue && profileAverageCriticRating.HasValue)
            {
                var diff = Math.Abs(itemCriticRating.Value - profileAverageCriticRating.Value);

                // Critic rating is 0-100 scale
                criticProximity = Math.Max(0, 1.0 - (diff / 100.0));
            }

            var ratingProximity = (communityProximity + criticProximity) / 2.0;
            var finalScore = ((1 - ratingProximityWeight) * cosineSimilarity) + (ratingProximityWeight * ratingProximity);

            return new TasteScore(cosineSimilarity, (float)communityProximity, (float)criticProximity, (float)ratingProximity, (float)finalScore);
        }
    }
}
