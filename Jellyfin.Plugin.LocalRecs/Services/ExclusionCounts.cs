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

        /// <summary>Gets the total number of excluded items.</summary>
        public int TotalExcluded => Inaccessible + NoMetadata + NotFound + Watched + SeriesWatched + InProgress;
    }
}
