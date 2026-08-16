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
}
