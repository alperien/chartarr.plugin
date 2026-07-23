using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace Chartarr.ImportLists.ChartCsv
{
    public class ChartCsvSettingsValidator : AbstractValidator<ChartCsvSettings>
    {
        public ChartCsvSettingsValidator()
        {
            RuleFor(c => c.PathOrUrl).NotEmpty()
                .WithMessage("a csv path or url is required");
            RuleFor(c => c.MinConfidence).InclusiveBetween(0, 100);
        }
    }

    public class ChartCsvSettings : IImportListSettings
    {
        private static readonly ChartCsvSettingsValidator Validator = new ChartCsvSettingsValidator();

        public string BaseUrl { get; set; } = "";

        [FieldDefinition(0, Label = "CSV Path or URL",
            HelpText = "Path to a csv readable by Lidarr, or an http(s) url. Needs an artist and a title/album column; other columns are ignored.")]
        public string PathOrUrl { get; set; } = "";

        [FieldDefinition(1, Label = "Use MusicBrainz matcher", Type = FieldType.Checkbox,
            HelpText = "Match rows against MusicBrainz before handing them to Lidarr. More accurate for odd titles, but the first sync of a big chart is slow (about one row per second). When off, rows are passed by name and Lidarr maps them itself.")]
        public bool UseMatcher { get; set; } = true;

        [FieldDefinition(2, Label = "Match confidence (%)", Type = FieldType.Number,
            HelpText = "Rows matched below this confidence are passed to Lidarr by name instead.")]
        public int MinConfidence { get; set; } = 80;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
