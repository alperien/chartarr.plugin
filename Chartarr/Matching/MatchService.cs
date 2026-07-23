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

    public interface IChartMatchService
    {
        MatchResult Match(string artist, string title, double minConfidence);
    }

    // runs the ported matcher with a persistent cache, so a chart is only
    // matched once; later list syncs are instant.
    public class ChartMatchService : IChartMatchService
    {
        private readonly MusicBrainzClient _client;
        private readonly Logger _logger;
        private readonly string _cachePath;
        private readonly object _cacheGate = new object();
        private Dictionary<string, MatchResult> _cache;

        public ChartMatchService(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _logger = logger;
            _client = new MusicBrainzClient(logger);
            _cachePath = Path.Combine(appFolderInfo.AppDataFolder, "chartarr-match-cache.json");
        }

        public MatchResult Match(string artist, string title, double minConfidence)
        {
            var key = Matcher.Normalize(artist) + "|" + Matcher.Normalize(title);
            lock (_cacheGate)
            {
                LoadCache();
                if (_cache.TryGetValue(key, out var cached))
                {
                    cached.Confident = cached.ReleaseGroupMbid != null
                                       && cached.Confidence >= minConfidence;
                    return cached;
                }
            }

            var result = MatchUncached(artist, title, minConfidence);
            lock (_cacheGate)
            {
                _cache[key] = result;
                SaveCache();
            }

            return result;
        }

        private MatchResult MatchUncached(string artist, string title, double minConfidence)
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
                var (candidates, scores) = _client.SearchReleaseGroups(query);
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
                return new MatchResult { Confidence = 0, Confident = false };
            }

            var confident = Matcher.IsConfident(best, minConfidence);
            _logger.Debug("chartarr match: {0} — {1} -> {2} ({3:F2}, confident: {4})",
                artist, title, best.MbTitle, best.Confidence, confident);

            return new MatchResult
            {
                ReleaseGroupMbid = confident ? best.ReleaseGroupMbid : null,
                ArtistMbid = confident ? best.ArtistMbid : null,
                Confidence = best.Confidence,
                Confident = confident,
            };
        }

        private void LoadCache()
        {
            if (_cache != null)
            {
                return;
            }

            _cache = new Dictionary<string, MatchResult>();
            try
            {
                if (File.Exists(_cachePath))
                {
                    _cache = JsonSerializer.Deserialize<Dictionary<string, MatchResult>>(
                                 File.ReadAllText(_cachePath))
                             ?? new Dictionary<string, MatchResult>();
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
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "could not write chartarr match cache");
            }
        }
    }
}
