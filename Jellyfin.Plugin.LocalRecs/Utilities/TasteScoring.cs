using System;

namespace Jellyfin.Plugin.LocalRecs.Utilities
{
    /// <summary>
    /// Result of blending content-vector similarity with rating proximity for a single
    /// taste-vector/item pair.
    /// </summary>
    public readonly struct TasteScore
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TasteScore"/> struct.
        /// </summary>
        /// <param name="cosineSimilarity">Raw cosine similarity between taste vector and item vector.</param>
        /// <param name="communityProximity">Community rating proximity component (0-1), or null when rating proximity is disabled.</param>
        /// <param name="criticProximity">Critic rating proximity component (0-1), or null when rating proximity is disabled.</param>
        /// <param name="ratingProximity">Blended rating proximity (average of community and critic), or null when rating proximity is disabled.</param>
        /// <param name="finalScore">Cosine similarity blended with rating proximity per configured weight.</param>
        public TasteScore(float cosineSimilarity, float? communityProximity, float? criticProximity, float? ratingProximity, float finalScore)
        {
            CosineSimilarity = cosineSimilarity;
            CommunityProximity = communityProximity;
            CriticProximity = criticProximity;
            RatingProximity = ratingProximity;
            FinalScore = finalScore;
        }

        /// <summary>Gets the raw cosine similarity between taste vector and item vector.</summary>
        public float CosineSimilarity { get; }

        /// <summary>Gets the community rating proximity component (0-1). Null when rating proximity is disabled.</summary>
        public float? CommunityProximity { get; }

        /// <summary>Gets the critic rating proximity component (0-1). Null when rating proximity is disabled.</summary>
        public float? CriticProximity { get; }

        /// <summary>Gets the blended rating proximity (average of community and critic). Null when rating proximity is disabled.</summary>
        public float? RatingProximity { get; }

        /// <summary>Gets the final score: cosine similarity blended with rating proximity.</summary>
        public float FinalScore { get; }
    }

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
