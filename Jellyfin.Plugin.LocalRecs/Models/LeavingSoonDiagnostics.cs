namespace Jellyfin.Plugin.LocalRecs.Models
{
    /// <summary>
    /// Skip-reason counters collected during a single Leaving Soon discovery pass.
    /// </summary>
    public class LeavingSoonDiagnostics
    {
        /// <summary>Gets or sets the number of user taste profiles used for scoring.</summary>
        public int EligibleProfileCount { get; set; }

        /// <summary>Gets or sets the number of collections protected from candidacy.</summary>
        public int SafeCollectionCount { get; set; }

        /// <summary>Gets or sets items skipped because they are already removal candidates.</summary>
        public int SkippedAlreadyRemoval { get; set; }

        /// <summary>Gets or sets items skipped due to missing metadata (no genres and no actors).</summary>
        public int SkippedNoMetadata { get; set; }

        /// <summary>Gets or sets items skipped because their collection is protected.</summary>
        public int SkippedSafeCollection { get; set; }

        /// <summary>Gets or sets items skipped because no embedding was computed for them.</summary>
        public int SkippedNoEmbedding { get; set; }

        /// <summary>Gets or sets items skipped because they are favorited or currently in-progress by any user.</summary>
        public int SkippedAlwaysSafe { get; set; }

        /// <summary>Gets or sets items skipped because they do not meet the minimum age requirement.</summary>
        public int SkippedTooYoung { get; set; }

        /// <summary>Gets or sets items skipped because the item could not be found in the library.</summary>
        public int SkippedNotFound { get; set; }

        /// <summary>Gets or sets the number of movie candidates that were scored.</summary>
        public int ScoredMovies { get; set; }

        /// <summary>Gets or sets the number of TV series candidates that were scored.</summary>
        public int ScoredTv { get; set; }
    }
}
