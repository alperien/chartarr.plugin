using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace Chartarr.Matching
{
    public class MatchResult
    {
        public string ReleaseGroupMbid { get; set; }
        public string ArtistMbid { get; set; }
        public double Confidence { get; set; }
        public bool Confident { get; set; }
    }

    // what gets persisted per row: the best candidate found, whatever its
    // confidence, so a threshold change re-evaluates without new lookups.
    // rows with no candidate at all (mbid null) are retried after a while,
    // so a musicbrainz outage can't poison the cache forever.
    public class CachedMatch
    {
        public string Mbid { get; set; }
        public string ArtistMbid { get; set; }
        public double Confidence { get; set; }
        public double TitleSim { get; set; }
        public double ArtistSim { get; set; }
        public DateTime CheckedUtc { get; set; }
    }

    public interface IChartMatchService
    {
        MatchResult Match(string artist, string title, double minConfidence);
        void Flush();
    }

    public class ChartMatchService : IChartMatchService
    {
        private static readonly TimeSpan MissRetryAfter = TimeSpan.FromDays(7);
        private const int SaveEvery = 25;

        private readonly Func<string, (List<Candidate> Candidates, Dictionary<string, int> Scores)> _search;
        private readonly Logger _logger;
        private readonly string _cachePath;
        private readonly object _gate = new object();
        private Dictionary<string, CachedMatch> _cache;
        private int _dirty;

        public ChartMatchService(IAppFolderInfo appFolderInfo, Logger logger)
            : this(appFolderInfo, logger, null)
        {
        }

        // test seam: inject a fake search
        internal ChartMatchService(IAppFolderInfo appFolderInfo, Logger logger,
                                   Func<string, (List<Candidate>, Dictionary<string, int>)> search)
        {
            _logger = logger;
            if (search == null)
            {
                var client = new MusicBrainzClient(logger);
                _search = q => client.SearchReleaseGroups(q);
            }
            else
            {
                _search = q => search(q);
            }

            _cachePath = Path.Combine(appFolderInfo.AppDataFolder, "chartarr-match-cache.json");
        }

        public MatchResult Match(string artist, string title, double minConfidence)
        {
            var key = Matcher.Normalize(artist) + "|" + Matcher.Normalize(title);
            lock (_gate)
            {
                LoadCache();
                if (_cache.TryGetValue(key, out var hit) && !IsStaleMiss(hit))
                {
                    return ToResult(hit, minConfidence);
                }
            }

            var found = MatchUncached(artist, title);
            lock (_gate)
            {
                _cache[key] = found;
                if (++_dirty >= SaveEvery)
                {
                    SaveCache();
                }
            }

            return ToResult(found, minConfidence);
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (_dirty > 0 && _cache != null)
                {
                    SaveCache();
                }
            }
        }

        private static bool IsStaleMiss(CachedMatch m)
        {
            return m.Mbid == null && DateTime.UtcNow - m.CheckedUtc > MissRetryAfter;
        }

        private static MatchResult ToResult(CachedMatch m, double minConfidence)
        {
            var confident = m.Mbid != null
                            && Matcher.IsConfident(m.TitleSim, m.ArtistSim, m.Confidence, minConfidence);
            return new MatchResult
            {
                ReleaseGroupMbid = confident ? m.Mbid : null,
                ArtistMbid = confident ? m.ArtistMbid : null,
                Confidence = m.Confidence,
                Confident = confident,
            };
        }

        private CachedMatch MatchUncached(string artist, string title)
        {
            var titleVariants = Matcher.Variants(title);
            var artistVariants = Matcher.Variants(artist);

            var queries = new List<string>
            {
                $"releasegroup:{MusicBrainzClient.LuceneQuote(titleVariants[0])} AND artist:{MusicBrainzClient.LuceneQuote(artistVariants[0])}"
            };
            foreach (var tv in titleVariants.Skip(1).Take(2))
            {
                queries.Add($"releasegroup:{MusicBrainzClient.LuceneQuote(tv)} AND artist:{MusicBrainzClient.LuceneQuote(artistVariants[0])}");
            }

            queries.Add(titleVariants[0] + " " + artistVariants[0]);

            var pool = new Dictionary<string, Candidate>();
            var poolScores = new Dictionary<string, int>();
            foreach (var query in queries)
            {
                var (candidates, scores) = _search(query);
                foreach (var kv in scores)
                {
                    poolScores[kv.Key] = Math.Max(kv.Value,
                        poolScores.TryGetValue(kv.Key, out var old) ? old : 0);
                }

                foreach (var c in Matcher.Score(candidates, titleVariants, artistVariants, poolScores))
                {
                    if (!pool.TryGetValue(c.ReleaseGroupMbid, out var cur) || c.SortKey > cur.SortKey)
                    {
                        pool[c.ReleaseGroupMbid] = c;
                    }
                }

                var bestSoFar = pool.Values.OrderByDescending(c => c.SortKey).FirstOrDefault();
                if (bestSoFar != null && bestSoFar.TitleSim >= 0.87 && bestSoFar.ArtistSim >= 0.75)
                {
                    break;
                }
            }

            var best = pool.Values.OrderByDescending(c => c.SortKey).FirstOrDefault();
            if (best == null)
            {
                return new CachedMatch { CheckedUtc = DateTime.UtcNow };
            }

            _logger.Debug("chartarr match: {0} — {1} -> {2} ({3:F2})",
                artist, title, best.MbTitle, best.Confidence);

            return new CachedMatch
            {
                Mbid = best.ReleaseGroupMbid,
                ArtistMbid = best.ArtistMbid,
                Confidence = Math.Round(best.Confidence, 3),
                TitleSim = Math.Round(best.TitleSim, 3),
                ArtistSim = Math.Round(best.ArtistSim, 3),
                CheckedUtc = DateTime.UtcNow,
            };
        }

        private void LoadCache()
        {
            if (_cache != null)
            {
                return;
            }

            _cache = new Dictionary<string, CachedMatch>();
            try
            {
                if (File.Exists(_cachePath))
                {
                    _cache = JsonSerializer.Deserialize<Dictionary<string, CachedMatch>>(
                                 File.ReadAllText(_cachePath))
                             ?? new Dictionary<string, CachedMatch>();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "could not read chartarr match cache; starting fresh");
            }
        }

        private void SaveCache()
        {
            try
            {
                File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
                _dirty = 0;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "could not write chartarr match cache");
            }
        }
    }
}
