using System;

namespace Jellyfin.Plugin.LocalRecs.Models
{
    /// <summary>
    /// Represents a ranked recommendation candidate with similarity score.
    /// </summary>
    public class ScoredRecommendation
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ScoredRecommendation"/> class.
        /// </summary>
        /// <param name="itemId">The item identifier.</param>
        /// <param name="score">The similarity score.</param>
        public ScoredRecommendation(Guid itemId, float score)
        {
            ItemId = itemId;
            Score = score;
        }

        /// <summary>
        /// Gets the item identifier.
        /// </summary>
        public Guid ItemId { get; }

        /// <summary>
        /// Gets the cosine similarity score (higher is better, range 0-1).
        /// </summary>
        public float Score { get; }

        /// <summary>Gets the raw cosine similarity before rating blending. Null for cold-start items.</summary>
        public float? CosineSimilarity { get; init; }

        /// <summary>Gets the community rating proximity component (0-1). Null when rating proximity is disabled.</summary>
        public float? CommunityProximity { get; init; }

        /// <summary>Gets the critic rating proximity component (0-1). Null when rating proximity is disabled.</summary>
        public float? CriticProximity { get; init; }

        /// <summary>Gets the blended rating proximity (average of community and critic). Null when rating proximity is disabled.</summary>
        public float? RatingProximity { get; init; }

        /// <summary>Gets the item's community rating (0-10). Null if not available.</summary>
        public float? ItemCommunityRating { get; init; }

        /// <summary>Gets the item's critic rating (0-100). Null if not available.</summary>
        public float? ItemCriticRating { get; init; }
    }
}
