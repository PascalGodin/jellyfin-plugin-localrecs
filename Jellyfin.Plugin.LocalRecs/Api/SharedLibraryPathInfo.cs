namespace Jellyfin.Plugin.LocalRecs.Api
{
    /// <summary>
    /// Shared virtual library path information for the Leaving Soon / Removal Candidates libraries.
    /// </summary>
    public class SharedLibraryPathInfo
    {
        /// <summary>
        /// Gets or sets the display name of the library.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the filesystem path for this library.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suggested library name to use in Jellyfin.
        /// </summary>
        public string SuggestedLibraryName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the Jellyfin library has been created for this path.
        /// </summary>
        public bool LibraryCreated { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this library is intended for administrators only.
        /// </summary>
        public bool IsAdminOnly { get; set; }
    }
}
