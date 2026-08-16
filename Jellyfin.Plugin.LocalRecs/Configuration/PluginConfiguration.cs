using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LocalRecs.Configuration
{
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            MovieRecommendationCount = 25;
            TvRecommendationCount = 25;
            FavoriteBoost = 2.0;
            RecencyDecayHalfLifeDays = 365.0;
            MinWatchedItemsForPersonalization = 3;
            MaxVocabularyActors = 500;
            MaxVocabularyDirectors = 0;
            MaxVocabularyTags = 500;
            NormalizeGenres = true;
            RecentWatchBoost = 1.0;
            EnableRatingProximity = true;
            RatingProximityWeight = 0.2;
            EnableDiagnosticLog = false;
            LeavingSoonEnabled = true;
            LeavingSoonMovieCount = 25;
            LeavingSoonTvCount = 25;
            LeavingSoonMinAgeDays = 180;
            LeavingSoonDwellDays = 30;
        }

        /// <summary>
        /// Gets or sets the number of movie recommendations to generate per user.
        /// </summary>
        public int MovieRecommendationCount { get; set; }

        /// <summary>
        /// Gets or sets the number of TV recommendations to generate per user.
        /// </summary>
        public int TvRecommendationCount { get; set; }

        /// <summary>
        /// Gets or sets the boost multiplier for favorite items.
        /// </summary>
        public double FavoriteBoost { get; set; }

        /// <summary>
        /// Gets or sets the recency decay half-life in days.
        /// </summary>
        public double RecencyDecayHalfLifeDays { get; set; }

        /// <summary>
        /// Gets or sets the minimum watched items required for personalization.
        /// </summary>
        public int MinWatchedItemsForPersonalization { get; set; }

        /// <summary>
        /// Gets or sets the maximum vocabulary size for actors (0 = unlimited).
        /// </summary>
        public int MaxVocabularyActors { get; set; }

        /// <summary>
        /// Gets or sets the maximum vocabulary size for directors (0 = unlimited).
        /// </summary>
        public int MaxVocabularyDirectors { get; set; }

        /// <summary>
        /// Gets or sets the maximum vocabulary size for tags (0 = unlimited).
        /// </summary>
        public int MaxVocabularyTags { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether known localized genre name variants (e.g. TMDB's
        /// French translations, such as "Comédie") are collapsed onto a single canonical English form
        /// before being added to the vocabulary. Disable this if you've fixed the root cause (e.g. a
        /// per-library metadata language override) and want to see raw genre strings as Jellyfin reports
        /// them. Default: true.
        /// </summary>
        public bool NormalizeGenres { get; set; }

        /// <summary>
        /// Gets or sets the recent recent watch boost scalar.
        /// Amplifies the weight of recently watched items relative to older items in the taste profile.
        /// Uses the existing recency decay: weight = decay × (1 + anchorBoost × decay).
        /// 0 = no boost (pure decay), 1 = just-watched items get 2× their decay weight (default).
        /// </summary>
        public double RecentWatchBoost { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to enable rating proximity weighting.
        /// When enabled, items with ratings closer to the user's average ratings get a boost.
        /// </summary>
        public bool EnableRatingProximity { get; set; }

        /// <summary>
        /// Gets or sets the weight for rating proximity in final score (0.0 to 1.0).
        /// 0.0 = pure content similarity, 1.0 = pure rating match.
        /// Default: 0.2 (20% rating proximity, 80% content similarity).
        /// </summary>
        public double RatingProximityWeight { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to write a diagnostic log after each recommendation refresh.
        /// The log includes per-item score breakdowns, exclusion details, and taste signals.
        /// Default: false.
        /// </summary>
        public bool EnableDiagnosticLog { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the Leaving Soon feature is enabled.
        /// </summary>
        public bool LeavingSoonEnabled { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of movies to flag in Leaving Soon.
        /// </summary>
        public int LeavingSoonMovieCount { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of TV series to flag in Leaving Soon.
        /// </summary>
        public int LeavingSoonTvCount { get; set; }

        /// <summary>
        /// Gets or sets the minimum item age in days before it can appear in Leaving Soon.
        /// Prevents recently-added items from immediately appearing.
        /// </summary>
        public int LeavingSoonMinAgeDays { get; set; }

        /// <summary>
        /// Gets or sets the number of days an item stays in Leaving Soon before being promoted to Removal Candidates.
        /// </summary>
        public int LeavingSoonDwellDays { get; set; }

        /// <summary>
        /// Validates the configuration and returns validation errors.
        /// </summary>
        /// <returns>List of validation error messages, empty if valid.</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();

            if (MovieRecommendationCount < 0)
            {
                errors.Add("MovieRecommendationCount must be non-negative");
            }

            if (TvRecommendationCount < 0)
            {
                errors.Add("TvRecommendationCount must be non-negative");
            }

            if (FavoriteBoost < 0)
            {
                errors.Add("FavoriteBoost must be non-negative");
            }

            if (RecencyDecayHalfLifeDays <= 0)
            {
                errors.Add("RecencyDecayHalfLifeDays must be positive");
            }

            if (MinWatchedItemsForPersonalization < 0)
            {
                errors.Add("MinWatchedItemsForPersonalization must be non-negative");
            }

            if (MaxVocabularyActors < 0)
            {
                errors.Add("MaxVocabularyActors must be non-negative (0 = unlimited)");
            }

            if (MaxVocabularyDirectors < 0)
            {
                errors.Add("MaxVocabularyDirectors must be non-negative (0 = unlimited)");
            }

            if (MaxVocabularyTags < 0)
            {
                errors.Add("MaxVocabularyTags must be non-negative (0 = unlimited)");
            }

            if (RecentWatchBoost < 0)
            {
                errors.Add("RecentWatchBoost must be non-negative");
            }

            if (RatingProximityWeight < 0 || RatingProximityWeight > 1)
            {
                errors.Add("RatingProximityWeight must be between 0.0 and 1.0");
            }

            if (LeavingSoonMovieCount < 0)
            {
                errors.Add("LeavingSoonMovieCount must be non-negative");
            }

            if (LeavingSoonTvCount < 0)
            {
                errors.Add("LeavingSoonTvCount must be non-negative");
            }

            if (LeavingSoonMinAgeDays < 0)
            {
                errors.Add("LeavingSoonMinAgeDays must be non-negative");
            }

            if (LeavingSoonDwellDays <= 0)
            {
                errors.Add("LeavingSoonDwellDays must be positive");
            }

            return errors;
        }
    }
}
