using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.Utilities;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit
{
    public class GenreNormalizerTests
    {
        [Theory]
        [InlineData("Comédie", "Comedy")]
        [InlineData("Aventure", "Adventure")]
        [InlineData("Drame", "Drama")]
        [InlineData("Familial", "Family")]
        [InlineData("Fantastique", "Fantasy")]
        [InlineData("Mystère", "Mystery")]
        [InlineData("Science-Fiction", "Science Fiction")]
        [InlineData("Science-Fiction & Fantastique", "Sci-Fi & Fantasy")]
        [InlineData("Action et Aventure", "Action & Adventure")]
        [InlineData("Enfants", "Kids")]
        public void Normalize_KnownFrenchVariant_ReturnsCanonicalEnglish(string french, string expectedEnglish)
        {
            GenreNormalizer.Normalize(french).Should().Be(expectedEnglish);
        }

        [Fact]
        public void Normalize_LowercaseVariant_IsMatchedCaseInsensitively()
        {
            GenreNormalizer.Normalize("comédie").Should().Be("Comedy");
        }

        [Theory]
        [InlineData("Comedy")]
        [InlineData("Action")]
        [InlineData("Anime")]
        [InlineData("Some Unknown Genre")]
        public void Normalize_UnknownOrAlreadyCanonicalGenre_ReturnsUnchanged(string genre)
        {
            GenreNormalizer.Normalize(genre).Should().Be(genre);
        }

        [Fact]
        public void Normalize_SurroundingWhitespace_IsTrimmed()
        {
            GenreNormalizer.Normalize("  Comédie  ").Should().Be("Comedy");
        }

        [Fact]
        public void Normalize_EmptyString_ReturnsEmptyString()
        {
            GenreNormalizer.Normalize(string.Empty).Should().Be(string.Empty);
        }

        [Fact]
        public void Normalize_FrenchAndEnglishVariants_CollapseToSameCanonicalForm()
        {
            // The whole point of the normalizer: two items tagged from different provider
            // locales end up on the same vocabulary dimension instead of two unrelated ones.
            GenreNormalizer.Normalize("Comédie").Should().Be(GenreNormalizer.Normalize("Comedy"));
            GenreNormalizer.Normalize("Mystère").Should().Be(GenreNormalizer.Normalize("Mystery"));
        }
    }
}
