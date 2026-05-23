using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>Records why candidates were excluded during recommendation filtering for a single media type.</summary>
    public sealed class ExclusionCounts
    {
        /// <summary>Gets the number of items excluded because the user lacks library access.</summary>
        public int Inaccessible { get; init; }

        /// <summary>Gets the number of items excluded due to missing genres and actors.</summary>
        public int NoMetadata { get; init; }

        /// <summary>Gets the number of items not found in the Jellyfin library.</summary>
        public int NotFound { get; init; }

        /// <summary>Gets the number of movies excluded because they are fully played.</summary>
        public int Watched { get; init; }

        /// <summary>Gets the number of series excluded because they have watched episodes.</summary>
        public int SeriesWatched { get; init; }

        /// <summary>Gets the number of items excluded because playback is in progress.</summary>
        public int InProgress { get; init; }

        /// <summary>Gets the number of items that passed all filters.</summary>
        public int Final { get; init; }

        /// <summary>Gets the names of items excluded due to missing genres and actors.</summary>
        public IReadOnlyList<string> NoMetadataItems { get; init; } = Array.Empty<string>();

        /// <summary>Gets the names of items not found in the Jellyfin library.</summary>
        public IReadOnlyList<string> NotFoundItems { get; init; } = Array.Empty<string>();

        /// <summary>Gets the total number of excluded items.</summary>
        public int TotalExcluded => Inaccessible + NoMetadata + NotFound + Watched + SeriesWatched + InProgress;
    }

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
