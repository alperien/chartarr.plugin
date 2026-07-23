using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Chartarr.Matching
{
    public class Candidate
    {
        public string ReleaseGroupMbid { get; set; }
        public string ArtistMbid { get; set; }
        public string MbTitle { get; set; }
        public string MbArtist { get; set; }
        public string PrimaryType { get; set; }
        public double TitleSim { get; set; }
        public double ArtistSim { get; set; }
        public double Confidence { get; set; }
        public double SortKey { get; set; }
    }

    // port of the chartarr python matcher; the scoring survived a 1395-album
    // chart at 88.6% auto-match, so the semantics here mirror it closely.
    public static class Matcher
    {
        public const double TitleAccept = 0.85;
        public const double ArtistAccept = 0.7;
        public const double ConfidenceAccept = 0.8;

        // comparison form: casefolded, no diacritics, no punctuation
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            var decomposed = s.Normalize(NormalizationForm.FormKD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                sb.Append(ch);
            }

            var lowered = sb.ToString().ToLowerInvariant().Replace("&", " and ");
            var cleaned = new StringBuilder(lowered.Length);
            foreach (var ch in lowered)
            {
                cleaned.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }

            return Regex.Replace(cleaned.ToString(), @"\s+", " ").Trim();
        }

        // similarity in [0, 1]; symbol-only strings ("★") normalize to
        // nothing, so those fall back to a raw comparison
        public static double Similarity(string a, string b)
        {
            var na = Normalize(a ?? "");
            var nb = Normalize(b ?? "");
            if (na.Length > 0 && nb.Length > 0)
            {
                return Ratio(na, nb);
            }

            var ra = Regex.Replace((a ?? "").ToLowerInvariant(), @"\s+", "");
            var rb = Regex.Replace((b ?? "").ToLowerInvariant(), @"\s+", "");
            if (ra.Length == 0 || rb.Length == 0)
            {
                return 0.0;
            }

            return Ratio(ra, rb);
        }

        // difflib.SequenceMatcher.ratio(): 2*M/T with M the total size of
        // matched blocks found by recursive longest-contiguous-match
        // (ratcliff/obershelp), T the combined length.
        public static double Ratio(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
            {
                return 1.0;
            }

            var matched = MatchedLength(a, 0, a.Length, b, 0, b.Length);
            return 2.0 * matched / (a.Length + b.Length);
        }

        private static int MatchedLength(string a, int aLo, int aHi, string b, int bLo, int bHi)
        {
            var (bestI, bestJ, bestSize) = LongestMatch(a, aLo, aHi, b, bLo, bHi);
            if (bestSize == 0)
            {
                return 0;
            }

            return bestSize
                   + MatchedLength(a, aLo, bestI, b, bLo, bestJ)
                   + MatchedLength(a, bestI + bestSize, aHi, b, bestJ + bestSize, bHi);
        }

        private static (int, int, int) LongestMatch(string a, int aLo, int aHi, string b, int bLo, int bHi)
        {
            // positions of each char in b[bLo..bHi)
            var b2j = new Dictionary<char, List<int>>();
            for (var j = bLo; j < bHi; j++)
            {
                if (!b2j.TryGetValue(b[j], out var list))
                {
                    b2j[b[j]] = list = new List<int>();
                }

                list.Add(j);
            }

            int bestI = aLo, bestJ = bLo, bestSize = 0;
            var j2len = new Dictionary<int, int>();
            for (var i = aLo; i < aHi; i++)
            {
                var newJ2Len = new Dictionary<int, int>();
                if (b2j.TryGetValue(a[i], out var positions))
                {
                    foreach (var j in positions)
                    {
                        var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                        newJ2Len[j] = k;
                        if (k > bestSize)
                        {
                            bestI = i - k + 1;
                            bestJ = j - k + 1;
                            bestSize = k;
                        }
                    }
                }

                j2len = newJ2Len;
            }

            return (bestI, bestJ, bestSize);
        }

        // alternate forms: the whole string, each line, bracket contents
        public static List<string> Variants(string s)
        {
            var outList = new List<string>();
            void Add(string x)
            {
                x = Regex.Replace(x ?? "", @"\s+", " ").Trim();
                if (x.Length > 0 && !outList.Contains(x))
                {
                    outList.Add(x);
                }
            }

            s ??= "";
            Add(s.Replace("\n", " "));
            foreach (var line in s.Split('\n'))
            {
                Add(line);
            }

            var flat = Regex.Replace(s, @"\s+", " ").Trim();
            var m = Regex.Match(flat, @"^(.*?)\s*\[([^\]]+)\]$");
            if (m.Success)
            {
                Add(m.Groups[1].Value);
                Add(m.Groups[2].Value);
            }

            m = Regex.Match(flat, @"^(.*?)\s*\(([^)]+)\)$");
            if (m.Success)
            {
                Add(m.Groups[1].Value);
            }

            return outList;
        }

        // score release groups against the variants, best first; albums beat
        // singles on ties (bowie has an album and a single both titled ★)
        public static List<Candidate> Score(IEnumerable<Candidate> rawCandidates,
                                            List<string> titleVariants,
                                            List<string> artistVariants,
                                            IReadOnlyDictionary<string, int> mbScores = null)
        {
            var scored = new List<Candidate>();
            foreach (var c in rawCandidates)
            {
                c.TitleSim = titleVariants.Count == 0 ? 0
                    : titleVariants.Max(tv => Similarity(tv, c.MbTitle));
                c.ArtistSim = artistVariants.Count == 0 ? 0
                    : artistVariants.Max(av => Similarity(av, c.MbArtist));
                c.Confidence = 0.55 * c.TitleSim + 0.45 * c.ArtistSim;
                c.SortKey = c.Confidence;
                if (c.PrimaryType == "Album")
                {
                    c.SortKey += 0.03;
                }
                else if (c.PrimaryType == "EP")
                {
                    c.SortKey += 0.015;
                }

                if (mbScores != null && c.ReleaseGroupMbid != null
                    && mbScores.TryGetValue(c.ReleaseGroupMbid, out var mbScore))
                {
                    c.SortKey += 0.0003 * mbScore;
                }

                scored.Add(c);
            }

            return scored.OrderByDescending(c => c.SortKey).ToList();
        }

        public static bool IsConfident(Candidate c, double minConfidence)
        {
            return c != null
                   && c.TitleSim >= TitleAccept
                   && c.ArtistSim >= ArtistAccept
                   && c.Confidence >= Math.Max(ConfidenceAccept, minConfidence);
        }
    }
}
