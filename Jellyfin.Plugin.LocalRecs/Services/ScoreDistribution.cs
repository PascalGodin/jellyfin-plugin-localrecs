namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>Score distribution statistics for all candidates scored before top-N truncation.</summary>
    public sealed class ScoreDistribution
    {
        /// <summary>Gets the number of candidates scored.</summary>
        public int CandidateCount { get; init; }

        /// <summary>Gets the minimum score across all candidates.</summary>
        public float MinScore { get; init; }

        /// <summary>Gets the maximum score across all candidates.</summary>
        public float MaxScore { get; init; }

        /// <summary>Gets the mean score across all candidates.</summary>
        public float MeanScore { get; init; }

        /// <summary>Gets the standard deviation of candidate scores.</summary>
        public float StdDev { get; init; }
    }
}
