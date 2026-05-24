using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalRecs.Models
{
    /// <summary>
    /// Persisted state for the Leaving Soon feature.
    /// Loaded at refresh start, written at refresh end. Never cleared between refreshes.
    /// </summary>
    public class LeavingSoonState
    {
        /// <summary>
        /// Gets or sets item IDs mapped to the UTC date they were first flagged as Leaving Soon.
        /// </summary>
        public Dictionary<string, DateTime> FlaggedItems { get; set; } = new Dictionary<string, DateTime>();

        /// <summary>
        /// Gets or sets item IDs mapped to the UTC date they were promoted to Removal Candidates.
        /// </summary>
        public Dictionary<string, DateTime> RemovalCandidates { get; set; } = new Dictionary<string, DateTime>();
    }
}
