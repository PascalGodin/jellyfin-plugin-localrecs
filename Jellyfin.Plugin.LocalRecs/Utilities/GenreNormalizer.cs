using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LocalRecs.Utilities
{
    /// <summary>
    /// Collapses known localized genre name variants (e.g. TMDB's French translations) onto a
    /// single canonical English form. Libraries scraped from mixed-locale metadata otherwise end
    /// up with "Comedy" and "Comédie" as two unrelated vocabulary dimensions, which fragments the
    /// content-similarity signal for every item tagged with the non-canonical variant.
    /// </summary>
    public static class GenreNormalizer
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // TMDB movie genres (French → English)
            ["Aventure"] = "Adventure",
            ["Comédie"] = "Comedy",
            ["Documentaire"] = "Documentary",
            ["Drame"] = "Drama",
            ["Familial"] = "Family",
            ["Fantastique"] = "Fantasy",
            ["Histoire"] = "History",
            ["Horreur"] = "Horror",
            ["Musique"] = "Music",
            ["Mystère"] = "Mystery",
            ["Science-Fiction"] = "Science Fiction",
            ["Téléfilm"] = "TV Movie",
            ["Guerre"] = "War",

            // TMDB TV genres (French → English), where they differ from the movie list above
            ["Action et Aventure"] = "Action & Adventure",
            ["Action & Aventure"] = "Action & Adventure",
            ["Enfants"] = "Kids",
            ["Actualités"] = "News",
            ["Télé-réalité"] = "Reality",
            ["Science-Fiction & Fantastique"] = "Sci-Fi & Fantasy",
            ["Feuilleton"] = "Soap",
            ["Talk-show"] = "Talk",
            ["Guerre et Politique"] = "War & Politics",
        };

        /// <summary>
        /// Returns the canonical form of a genre name, or the trimmed input unchanged if it has no
        /// known alias.
        /// </summary>
        /// <param name="genre">The raw genre string as reported by Jellyfin.</param>
        /// <returns>The canonical genre name.</returns>
        public static string Normalize(string genre)
        {
            if (string.IsNullOrWhiteSpace(genre))
            {
                return genre;
            }

            var trimmed = genre.Trim();
            return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
        }
    }
}
