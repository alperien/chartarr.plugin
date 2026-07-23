using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using NLog;

namespace Chartarr.Matching
{
    // rate-limited release-group search against musicbrainz. one request per
    // second per their api rules; only throttling and server errors retry.
    public class MusicBrainzClient
    {
        private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(1100);
        private static readonly object Gate = new object();
        private static DateTime _lastRequest = DateTime.MinValue;

        private readonly Logger _logger;

        public MusicBrainzClient(Logger logger)
        {
            _logger = logger;
        }

        public (List<Candidate> Candidates, Dictionary<string, int> Scores) SearchReleaseGroups(string query, int limit = 8)
        {
            var candidates = new List<Candidate>();
            var scores = new Dictionary<string, int>();
            var url = "https://musicbrainz.org/ws/2/release-group/?query="
                      + Uri.EscapeDataString(query) + "&fmt=json&limit=" + limit;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                Pace();
                try
                {
                    using var response = PluginHttp.Client.GetAsync(url).GetAwaiter().GetResult();
                    var status = (int)response.StatusCode;
                    if (status == 429 || status == 503)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta
                                         ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                        Thread.Sleep(retryAfter > TimeSpan.FromSeconds(30)
                            ? TimeSpan.FromSeconds(30) : retryAfter);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // 400s and friends won't get better on retry
                        _logger.Warn("musicbrainz returned {0} for query {1}", status, query);
                        break;
                    }

                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Parse(body, candidates, scores);
                    return (candidates, scores);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "musicbrainz search attempt {0} failed", attempt + 1);
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                }
            }

            return (candidates, scores);
        }

        private static void Pace()
        {
            lock (Gate)
            {
                var wait = _lastRequest + MinSpacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }

                _lastRequest = DateTime.UtcNow;
            }
        }

        internal static void Parse(string json, List<Candidate> candidates, Dictionary<string, int> scores)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("release-groups", out var groups))
            {
                return;
            }

            foreach (var rg in groups.EnumerateArray())
            {
                var id = rg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (id == null)
                {
                    continue;
                }

                var credit = "";
                string artistMbid = null;
                if (rg.TryGetProperty("artist-credit", out var credits))
                {
                    foreach (var c in credits.EnumerateArray())
                    {
                        var name = c.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        if (c.TryGetProperty("artist", out var artistEl))
                        {
                            name ??= artistEl.TryGetProperty("name", out var an) ? an.GetString() : "";
                            if (artistMbid == null && artistEl.TryGetProperty("id", out var aid))
                            {
                                artistMbid = aid.GetString();
                            }
                        }

                        credit += name ?? "";
                        if (c.TryGetProperty("joinphrase", out var join))
                        {
                            credit += join.GetString() ?? "";
                        }
                    }
                }

                candidates.Add(new Candidate
                {
                    ReleaseGroupMbid = id,
                    ArtistMbid = artistMbid,
                    MbTitle = rg.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    MbArtist = credit.Trim(),
                    PrimaryType = rg.TryGetProperty("primary-type", out var pt) ? pt.GetString() : null,
                });

                if (rg.TryGetProperty("score", out var scoreEl)
                    && scoreEl.TryGetInt32(out var score))
                {
                    scores[id] = score;
                }
            }
        }

        public static string LuceneQuote(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
