using System;
using System.Collections.Generic;
using System.Linq;
using Chartarr.Matching;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chartarr.ImportLists.ChartCsv
{
    public class ChartCsvImport : ImportListBase<ChartCsvSettings>
    {
        private readonly IChartMatchService _matchService;

        public override string Name => "Chart CSV";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public ChartCsvImport(IChartMatchService matchService,
                              IImportListStatusService importListStatusService,
                              IConfigService configService,
                              IParsingService parsingService,
                              Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _matchService = matchService;
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var items = new List<ImportListItemInfo>();
            try
            {
                var rows = ChartCsvParser.Parse(Settings.PathOrUrl);
                _logger.Info("chart csv: {0} rows from {1}", rows.Count, Settings.PathOrUrl);

                foreach (var (artist, album) in rows)
                {
                    var item = new ImportListItemInfo
                    {
                        Artist = FirstLine(artist),
                        Album = FirstLine(album),
                    };

                    if (Settings.UseMatcher)
                    {
                        var match = _matchService.Match(artist, album, Settings.MinConfidence / 100.0);
                        if (match.Confident)
                        {
                            item.AlbumMusicBrainzId = match.ReleaseGroupMbid;
                            item.ArtistMusicBrainzId = match.ArtistMbid;
                        }
                    }

                    items.Add(item);
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "failed to fetch chart csv for list {0}", Definition.Name);
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                var rows = ChartCsvParser.Parse(Settings.PathOrUrl);
                if (rows.Count == 0)
                {
                    failures.Add(new ValidationFailure("PathOrUrl", "no usable rows found"));
                }
            }
            catch (Exception ex)
            {
                failures.Add(new ValidationFailure("PathOrUrl", ex.Message));
            }
        }

        private static string FirstLine(string s)
        {
            var idx = s.IndexOf('\n');
            return idx < 0 ? s : s.Substring(0, idx).Trim();
        }
    }
}
