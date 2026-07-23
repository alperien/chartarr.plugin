using System.Collections.Generic;
using Chartarr.ImportLists.ChartCsv;
using Chartarr.Matching;
using Xunit;

namespace Chartarr.Tests
{
    // the same golden cases as the chartarr cli; every one happened on a
    // real 1395-album chart.
    public class MatcherTests
    {
        [Fact]
        public void normalize_strips_diacritics_and_punctuation()
        {
            Assert.Equal(Matcher.Normalize("sigur ros"), Matcher.Normalize("Sigur Rós"));
            Assert.Equal(Matcher.Normalize("pinata"), Matcher.Normalize("Piñata"));
            Assert.Equal(Matcher.Normalize("Good Kid, M.A.A.D City"),
                         Matcher.Normalize("good kid, m.A.A.d city"));
        }

        [Fact]
        public void normalize_treats_ampersand_as_and()
        {
            Assert.Equal(Matcher.Normalize("Freddie Gibbs and Madlib"),
                         Matcher.Normalize("Freddie Gibbs & Madlib"));
        }

        [Fact]
        public void normalize_symbol_only_is_empty()
        {
            Assert.Equal("", Matcher.Normalize("★"));
        }

        [Fact]
        public void similarity_symbol_only_titles_compare_raw()
        {
            Assert.Equal(1.0, Matcher.Similarity("★", "★"), 3);
            Assert.True(Matcher.Similarity("★", "✝") < 1.0);
        }

        [Fact]
        public void similarity_sharp_signs_and_infinity()
        {
            // rym writes F♯A♯∞, musicbrainz writes F♯ A♯ ∞
            Assert.True(Matcher.Similarity("F♯A♯∞", "F♯ A♯ ∞") > 0.7);
        }

        [Fact]
        public void similarity_punctuation_in_artist_names()
        {
            Assert.Equal(1.0,
                Matcher.Similarity("Godspeed You Black Emperor!", "Godspeed You! Black Emperor"), 3);
        }

        [Fact]
        public void ratio_matches_difflib_known_values()
        {
            // cross-checked against python difflib.SequenceMatcher.ratio()
            Assert.Equal(0.75, Matcher.Ratio("abcd", "bcde"), 3);
            Assert.Equal(1.0, Matcher.Ratio("abc", "abc"), 3);
            Assert.Equal(0.0, Matcher.Ratio("abc", "xyz"), 3);
        }

        [Fact]
        public void variants_dual_script_title()
        {
            var vs = Matcher.Variants("98.12.28 Otokotachi no wakare\n98.12.28 男達の別れ");
            Assert.Contains("98.12.28 Otokotachi no wakare", vs);
            Assert.Contains("98.12.28 男達の別れ", vs);
        }

        [Fact]
        public void variants_bracketed_alt_title()
        {
            var vs = Matcher.Variants("★ [Blackstar]");
            Assert.Contains("★", vs);
            Assert.Contains("Blackstar", vs);
        }

        [Fact]
        public void blackstar_album_beats_single_at_equal_similarity()
        {
            // bowie has an album and a single both titled ★; the album must win
            var candidates = new List<Candidate>
            {
                new Candidate { ReleaseGroupMbid = "single-id", MbTitle = "★", MbArtist = "David Bowie", PrimaryType = "Single" },
                new Candidate { ReleaseGroupMbid = "album-id", MbTitle = "★", MbArtist = "David Bowie", PrimaryType = "Album" },
            };
            var ranked = Matcher.Score(candidates,
                Matcher.Variants("★ [Blackstar]"), Matcher.Variants("David Bowie"));
            Assert.Equal("album-id", ranked[0].ReleaseGroupMbid);
            Assert.Equal(1.0, ranked[0].TitleSim, 3);
        }

        [Fact]
        public void exact_match_beats_near_match()
        {
            var candidates = new List<Candidate>
            {
                new Candidate { ReleaseGroupMbid = "demos-id", MbTitle = "Twin Fantasy Demos", MbArtist = "Car Seat Headrest", PrimaryType = "Album" },
                new Candidate { ReleaseGroupMbid = "real-id", MbTitle = "Twin Fantasy", MbArtist = "Car Seat Headrest", PrimaryType = "Album" },
            };
            var ranked = Matcher.Score(candidates,
                Matcher.Variants("Twin Fantasy"), Matcher.Variants("Car Seat Headrest"));
            Assert.Equal("real-id", ranked[0].ReleaseGroupMbid);
        }
    }

    public class ChartCsvParserTests
    {
        private static string Write(string content)
        {
            var path = System.IO.Path.GetTempFileName();
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void parses_rym_style_columns()
        {
            var rows = ChartCsvParser.Parse(Write("rank,title,artist\n1,OK Computer,Radiohead\n"));
            Assert.Single(rows);
            Assert.Equal(("Radiohead", "OK Computer"), rows[0]);
        }

        [Fact]
        public void parses_quoted_multiline_titles()
        {
            var csv = "title,artist\n\"98.12.28 Otokotachi no wakare\n98.12.28 男達の別れ\",Fishmans\n";
            var rows = ChartCsvParser.Parse(Write(csv));
            Assert.Single(rows);
            Assert.Equal("Fishmans", rows[0].Artist);
            Assert.Contains("男達の別れ", rows[0].Album);
        }

        [Fact]
        public void parses_quoted_commas_and_escaped_quotes()
        {
            var csv = "album,artist\n\"Good Kid, \"\"M.A.A.D\"\" City\",Kendrick Lamar\n";
            var rows = ChartCsvParser.Parse(Write(csv));
            Assert.Equal("Good Kid, \"M.A.A.D\" City", rows[0].Album);
        }

        [Fact]
        public void rejects_missing_columns()
        {
            Assert.Throws<System.IO.InvalidDataException>(
                () => ChartCsvParser.Parse(Write("year,thing\n1977,x\n")));
        }

        [Fact]
        public void skips_blank_rows()
        {
            var rows = ChartCsvParser.Parse(Write("title,artist\nRumours,Fleetwood Mac\n,\n"));
            Assert.Single(rows);
        }
    }
}
