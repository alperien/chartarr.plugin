using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Chartarr.ImportLists.ChartCsv
{
    public static class ChartCsvParser
    {
        private static readonly string[] ArtistCols = { "artist", "artists", "artist_name", "albumartist", "album artist" };
        private static readonly string[] TitleCols = { "title", "album", "album_title", "release", "name" };

        public static List<(string Artist, string Album)> Parse(string pathOrUrl)
        {
            var text = Read(pathOrUrl);
            var records = SplitRecords(text);
            if (records.Count < 2)
            {
                throw new InvalidDataException("csv has no data rows");
            }

            var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            var artistIdx = header.FindIndex(h => ArtistCols.Contains(h));
            var titleIdx = header.FindIndex(h => TitleCols.Contains(h));
            if (artistIdx < 0 || titleIdx < 0)
            {
                throw new InvalidDataException(
                    "need an artist and a title/album column, found: " + string.Join(", ", header));
            }

            var rows = new List<(string, string)>();
            foreach (var record in records.Skip(1))
            {
                if (record.Count <= Math.Max(artistIdx, titleIdx))
                {
                    continue;
                }

                var artist = record[artistIdx].Trim();
                var album = record[titleIdx].Trim();
                if (artist.Length > 0 && album.Length > 0)
                {
                    rows.Add((artist, album));
                }
            }

            return rows;
        }

        private static string Read(string pathOrUrl)
        {
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // the shared client caps the response size, so a wrong url
                // can't balloon memory
                return PluginHttp.Client.GetStringAsync(pathOrUrl).GetAwaiter().GetResult();
            }

            return File.ReadAllText(pathOrUrl);
        }

        // char-stream csv: quoted fields may contain commas, escaped quotes
        // and embedded newlines (rateyourmusic exports use all three)
        private static List<List<string>> SplitRecords(string text)
        {
            var records = new List<List<string>>();
            var record = new List<string>();
            var cell = new System.Text.StringBuilder();
            var quoted = false;

            void EndCell()
            {
                record.Add(cell.ToString());
                cell.Clear();
            }

            void EndRecord()
            {
                EndCell();
                if (record.Count > 1 || record[0].Trim().Length > 0)
                {
                    records.Add(record);
                }

                record = new List<string>();
            }

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else if (ch == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        cell.Append(ch);
                    }
                }
                else if (ch == '"')
                {
                    quoted = true;
                }
                else if (ch == ',')
                {
                    EndCell();
                }
                else if (ch == '\r')
                {
                    // swallow; \n handles the record break
                }
                else if (ch == '\n')
                {
                    EndRecord();
                }
                else
                {
                    cell.Append(ch);
                }
            }

            if (cell.Length > 0 || record.Count > 0)
            {
                EndRecord();
            }

            return records;
        }
    }
}
