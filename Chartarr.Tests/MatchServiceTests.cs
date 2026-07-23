using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Chartarr.Matching;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using Xunit;

namespace Chartarr.Tests
{
    internal class TempFolders : IAppFolderInfo
    {
        public TempFolders()
        {
            AppDataFolder = Path.Combine(Path.GetTempPath(), "chartarr-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AppDataFolder);
        }

        public string AppDataFolder { get; }
        public string TempFolder => Path.GetTempPath();
        public string StartUpFolder => Path.GetTempPath();
    }

    public class MatchServiceTests
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static Candidate Cand(string mbid, string title, string artist)
        {
            return new Candidate
            {
                ReleaseGroupMbid = mbid, ArtistMbid = "artist-" + mbid,
                MbTitle = title, MbArtist = artist, PrimaryType = "Album",
            };
        }

        [Fact]
        public void unconfident_match_keeps_its_candidate_for_lower_thresholds()
        {
            // title similarity 0.9, artist 1.0 -> confidence 0.945
            var calls = 0;
            var service = new ChartMatchService(new TempFolders(), Log, q =>
            {
                calls++;
                return (new List<Candidate> { Cand("rg-1", "abcdefghixy", "same artist") },
                        new Dictionary<string, int>());
            });

            var strict = service.Match("same artist", "abcdefghi", 0.99);
            Assert.False(strict.Confident);
            Assert.Null(strict.ReleaseGroupMbid);

            var relaxed = service.Match("same artist", "abcdefghi", 0.90);
            Assert.True(relaxed.Confident);
            Assert.Equal("rg-1", relaxed.ReleaseGroupMbid);
            Assert.Equal(1, calls); // second answer came from the cache
        }

        [Fact]
        public void misses_are_not_poisoned_forever()
        {
            var folders = new TempFolders();
            var calls = 0;
            var service = new ChartMatchService(folders, Log, q =>
            {
                calls++;
                return (new List<Candidate>(), new Dictionary<string, int>());
            });

            Assert.False(service.Match("nobody", "ghost album", 0.8).Confident);
            Assert.False(service.Match("nobody", "ghost album", 0.8).Confident);
            var callsAfterFreshMiss = calls; // fresh miss is served from cache

            // age the cached miss past the retry window and reload
            service.Flush();
            var cachePath = Path.Combine(folders.AppDataFolder, "chartarr-match-cache.json");
            var cache = JsonSerializer.Deserialize<Dictionary<string, CachedMatch>>(
                File.ReadAllText(cachePath));
            foreach (var entry in cache.Values)
            {
                entry.CheckedUtc = DateTime.UtcNow - TimeSpan.FromDays(8);
            }

            File.WriteAllText(cachePath, JsonSerializer.Serialize(cache));

            var reloaded = new ChartMatchService(folders, Log, q =>
            {
                calls++;
                return (new List<Candidate>(), new Dictionary<string, int>());
            });
            reloaded.Match("nobody", "ghost album", 0.8);
            Assert.True(calls > callsAfterFreshMiss); // the stale miss was retried
        }

        [Fact]
        public void flush_writes_pending_entries()
        {
            var folders = new TempFolders();
            var service = new ChartMatchService(folders, Log, q =>
                (new List<Candidate> { Cand("rg-1", "Rumours", "Fleetwood Mac") },
                 new Dictionary<string, int>()));

            service.Match("Fleetwood Mac", "Rumours", 0.8);
            var cachePath = Path.Combine(folders.AppDataFolder, "chartarr-match-cache.json");
            Assert.False(File.Exists(cachePath)); // below the batch size, nothing written yet

            service.Flush();
            Assert.True(File.Exists(cachePath));
            Assert.Contains("rg-1", File.ReadAllText(cachePath));
        }

        [Fact]
        public void threshold_is_honored_not_floored()
        {
            // confidence 0.7825 sits below the old hard floor of 0.8;
            // a user threshold of 0.75 must accept it now
            Assert.True(Matcher.IsConfident(0.85, 0.7, 0.7825, 0.75));
            Assert.False(Matcher.IsConfident(0.85, 0.7, 0.7825, 0.80));
        }
    }

    public class MusicBrainzParseTests
    {
        private const string Fixture = @"{
            ""release-groups"": [
                {
                    ""id"": ""rg-id"",
                    ""title"": ""OK Computer"",
                    ""primary-type"": ""Album"",
                    ""score"": 100,
                    ""artist-credit"": [
                        { ""name"": ""Radiohead"", ""artist"": { ""id"": ""a-id"", ""name"": ""Radiohead"" } }
                    ]
                },
                { ""title"": ""no id, skipped"" }
            ]
        }";

        [Fact]
        public void parses_candidates_and_scores()
        {
            var candidates = new List<Candidate>();
            var scores = new Dictionary<string, int>();
            MusicBrainzClient.Parse(Fixture, candidates, scores);

            var c = Assert.Single(candidates);
            Assert.Equal("rg-id", c.ReleaseGroupMbid);
            Assert.Equal("a-id", c.ArtistMbid);
            Assert.Equal("Radiohead", c.MbArtist);
            Assert.Equal("Album", c.PrimaryType);
            Assert.Equal(100, scores["rg-id"]);
        }

        [Fact]
        public void joins_multi_artist_credits()
        {
            const string multi = @"{ ""release-groups"": [ {
                ""id"": ""rg-2"", ""title"": ""Piñata"", ""primary-type"": ""Album"",
                ""artist-credit"": [
                    { ""name"": ""Freddie Gibbs"", ""joinphrase"": "" & "", ""artist"": { ""id"": ""a1"", ""name"": ""Freddie Gibbs"" } },
                    { ""name"": ""Madlib"", ""artist"": { ""id"": ""a2"", ""name"": ""Madlib"" } }
                ] } ] }";
            var candidates = new List<Candidate>();
            MusicBrainzClient.Parse(multi, candidates, new Dictionary<string, int>());
            Assert.Equal("Freddie Gibbs & Madlib", candidates[0].MbArtist);
            Assert.Equal("a1", candidates[0].ArtistMbid);
        }
    }
}
