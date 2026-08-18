using System;

namespace Jellyfin.Plugin.LocalRecs.Api
{
    /// <summary>
    /// User library path information for setup UI.
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
        /// Gets or sets the Removal Candidates movies library path.
        /// </summary>
        public string RemovalCandidatesMovieLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Removal Candidates TV library path.
        /// </summary>
        public string RemovalCandidatesTvLibraryPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested Removal Candidates movies library name.
        /// </summary>
        public string SuggestedRemovalCandidatesMovieLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested Removal Candidates TV library name.
        /// </summary>
        public string SuggestedRemovalCandidatesTvLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether all six libraries are set up.
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

        /// <summary>
        /// Gets or sets a value indicating whether the Removal Candidates Movies library exists.
        /// </summary>
        public bool RemovalCandidatesMovieLibraryCreated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Removal Candidates TV library exists.
        /// </summary>
        public bool RemovalCandidatesTvLibraryCreated { get; set; }
    }
}
