using System.Text.Json;
using lib.io;
using terraform.lib.engine;
using terraform.lib.engine.actions;

namespace terraform.Tests;

public sealed class FileFilterConfigTests
{
    [Fact]
    public void No_restrictions_means_no_filter()
    {
        Assert.Null(new FileFilterConfig().Build());
    }

    [Fact]
    public void Reads_the_documented_fields_from_json()
    {
        var config = ActionConfig.Read<FileFilterConfig>(Json.Element("""
            { "pattern": "^a.*", "matchType": "regex", "ignoreCase": false, "matchFullPath": true,
              "dateAfter": "30d", "dateBefore": "2024-01-15:c", "minSize": "10k", "maxSize": "5m" }
            """));

        Assert.NotNull(config.Build());
        Assert.False(config.IgnoreCase);
    }

    [Fact]
    public void Rejects_unknown_keys_and_unknown_match_types()
    {
        Assert.Throws<ActionConfigException>(() => ActionConfig.Read<FileFilterConfig>(Json.Element("""{ "patern": "x" }""")));
        Assert.Throws<ActionConfigException>(() => ActionConfig.Read<FileFilterConfig>(Json.Element("""{ "matchType": "Wildcard" }""")));
    }

    [Fact]
    public void Rejects_regex_constructs_the_non_backtracking_engine_cannot_run()
    {
        var config = new FileFilterConfig { Pattern = "(?=a)a", MatchType = PatternMatchType.Regex };

        var ex = Assert.Throws<ActionConfigException>(() => config.Build());

        Assert.Contains("filter.pattern", ex.Message);
    }

    [Fact]
    public void Rejects_invalid_sizes_and_dates_naming_the_field()
    {
        var size = Assert.Throws<ActionConfigException>(() => new FileFilterConfig { MinSize = "10x" }.Build());
        var date = Assert.Throws<ActionConfigException>(() => new FileFilterConfig { DateAfter = "yesterday" }.Build());

        Assert.Contains("filter.minSize", size.Message);
        Assert.Contains("filter.dateAfter", date.Message);
    }
}
