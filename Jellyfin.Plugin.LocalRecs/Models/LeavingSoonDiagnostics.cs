using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalRecs.Models
{
    /// <summary>
    /// Skip-reason counters collected during a single Leaving Soon discovery pass.
    /// </summary>
    public class LeavingSoonDiagnostics
    {
        /// <summary>Gets or sets the number of user taste profiles used for scoring.</summary>
        public int EligibleProfileCount { get; set; }

        /// <summary>Gets or sets items skipped because they are already removal candidates.</summary>
        public int SkippedAlreadyRemoval { get; set; }

        /// <summary>Gets or sets items skipped due to missing metadata (no genres and no actors).</summary>
        public int SkippedNoMetadata { get; set; }

        /// <summary>Gets or sets items skipped because no embedding was computed for them.</summary>
        public int SkippedNoEmbedding { get; set; }

        /// <summary>Gets or sets items skipped because they are favorited by any user.</summary>
        public int SkippedAlwaysSafe { get; set; }

        /// <summary>Gets or sets the count of worst-X candidates that failed the age gate.</summary>
        public int SkippedTooYoung { get; set; }

        /// <summary>Gets the worst-X candidates that failed the age gate, with days remaining until eligible.</summary>
        public List<(Guid ItemId, double DaysUntilEligible)> TooYoungItems { get; } = new List<(Guid ItemId, double DaysUntilEligible)>();

        /// <summary>Gets or sets items skipped because the item could not be found in the library.</summary>
        public int SkippedNotFound { get; set; }

        /// <summary>Gets or sets items that passed all checks but had an unrecognized media type (neither Movie nor Series).</summary>
        public int SkippedUnknownType { get; set; }

        /// <summary>Gets or sets the number of movie candidates that were scored.</summary>
        public int ScoredMovies { get; set; }

        /// <summary>Gets or sets the number of TV series candidates that were scored.</summary>
        public int ScoredTv { get; set; }
    }
}
