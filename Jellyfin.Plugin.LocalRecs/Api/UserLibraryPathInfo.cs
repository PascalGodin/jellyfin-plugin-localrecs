using System;

namespace Jellyfin.Plugin.LocalRecs.Api
{
    /// <summary>
    /// User library path information for setup UI. Covers only the per-user libraries
    /// (Recommended, Leaving Soon) — Removal Candidates is global, see <see cref="GlobalLibraryPathInfo"/>.
    /// </summary>
    public class UserLibraryPathInfo
    {
        /// <summary>
        /// Gets or sets the user ID.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the username.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the movie library path.
        /// </summary>
        public string MovieLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the TV library path.
        /// </summary>
        public string TvLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested movie library name.
        /// </summary>
        public string SuggestedMovieLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested TV library name.
        /// </summary>
        public string SuggestedTvLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Leaving Soon movies library path.
        /// </summary>
        public string LeavingSoonMovieLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Leaving Soon TV library path.
        /// </summary>
        public string LeavingSoonTvLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested Leaving Soon movies library name.
        /// </summary>
        public string SuggestedLeavingSoonMovieLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested Leaving Soon TV library name.
        /// </summary>
        public string SuggestedLeavingSoonTvLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether all four per-user libraries are set up.
        /// </summary>
        public bool LibrariesCreated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Recommended Movies library exists.
        /// </summary>
        public bool MovieLibraryCreated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Recommended TV library exists.
        /// </summary>
        public bool TvLibraryCreated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Leaving Soon Movies library exists.
        /// </summary>
        public bool LeavingSoonMovieLibraryCreated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Leaving Soon TV library exists.
        /// </summary>
        public bool LeavingSoonTvLibraryCreated { get; set; }
    }
}
