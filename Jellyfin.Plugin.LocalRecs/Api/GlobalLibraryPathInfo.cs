namespace Jellyfin.Plugin.LocalRecs.Api
{
    /// <summary>
    /// Global (non-per-user) library path information for setup UI. Currently just Removal
    /// Candidates, which is an admin-only deletion worklist rather than user-facing content.
    /// </summary>
    public class GlobalLibraryPathInfo
    {
        /// <summary>
        /// Gets or sets the Removal Candidates movies library path.
        /// </summary>
        public string RemovalCandidatesMoviesPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Removal Candidates TV library path.
        /// </summary>
        public string RemovalCandidatesTvPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether both Removal Candidates libraries exist.
        /// </summary>
        public bool LibrariesCreated { get; set; }
    }
}
