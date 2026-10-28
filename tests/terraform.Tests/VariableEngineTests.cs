using System.Text.Json;
using terraform.lib.engine;

namespace terraform.Tests;

public class VariableEngineTests
{
    private static VariableEngine Engine(params (string Name, string Value)[] variables) =>
        new(variables.ToDictionary(v => v.Name, v => v.Value));

    [Fact]
    public void Resolve_substitutes_string_values_at_any_depth_and_leaves_everything_else_alone()
    {
        var engine = Engine(("RootDir", @"D:\gruppotesi"), ("Name", "alpha"));
        var config = Json.Element("""{"paths":["{RootDir}\\dll"],"count":5,"flag":true,"nothing":null,"nested":{"who":"{Name}"},"{Name}":"key"}""");

        var resolved = engine.Resolve(config);

        Assert.Equal(@"D:\gruppotesi\dll", resolved.GetProperty("paths")[0].GetString());
        Assert.Equal(5, resolved.GetProperty("count").GetInt32());
        Assert.True(resolved.GetProperty("flag").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("nothing").ValueKind);
        Assert.Equal("alpha", resolved.GetProperty("nested").GetProperty("who").GetString());
        Assert.Equal("key", resolved.GetProperty("{Name}").GetString());
    }

    [Fact]
    public void Resolve_keeps_backslashes_and_quotes_of_the_substituted_value()
    {
        var engine = Engine(("Path", "C:\\dir\\\"quoted\""));

        var resolved = engine.Resolve(Json.Element("""{"p":"{Path}"}"""));

        Assert.Equal("C:\\dir\\\"quoted\"", resolved.GetProperty("p").GetString());
    }

    [Fact]
    public void Resolve_ignores_regex_quantifiers_and_guid_braces()
    {
        var engine = Engine(("A", "x"));
        var config = Json.Element("""{"pattern":"^a{2}b{1,3}$","guid":"{8F4B1C2A-1234-5678-9ABC-DEF012345678}"}""");

        var resolved = engine.Resolve(config);

        Assert.Equal("^a{2}b{1,3}$", resolved.GetProperty("pattern").GetString());
        Assert.Equal("{8F4B1C2A-1234-5678-9ABC-DEF012345678}", resolved.GetProperty("guid").GetString());
    }

    [Fact]
    public void Resolve_reports_every_unknown_token_with_its_json_path()
    {
        var engine = Engine(("Known", "k"));
        var config = Json.Element("""{"a":"{Missing1}","b":["ok","{Known}-{Missing2}"]}""");

        var ex = Assert.Throws<VariableResolutionException>(() => engine.Resolve(config, path: "step1.config"));

        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains(ex.Errors, e => e.StartsWith("step1.config.a:") && e.Contains("{Missing1}"));
        Assert.Contains(ex.Errors, e => e.StartsWith("step1.config.b[1]:") && e.Contains("{Missing2}"));
    }

    [Fact]
    public void Resolve_leaves_deferred_tokens_for_the_action()
    {
        var engine = Engine(("RootDir", "D:"));
        var deferred = new HashSet<string> { "FilePath" };

        var resolved = engine.Resolve(Json.Element("""{"args":"/s \"{FilePath}\" {RootDir}"}"""), deferred);

        Assert.Equal("/s \"{FilePath}\" D:", resolved.GetProperty("args").GetString());
    }

    [Fact]
    public void Resolve_rejects_per_file_tokens_when_the_action_does_not_declare_them()
    {
        var ex = Assert.Throws<VariableResolutionException>(() => Engine().Resolve(Json.Element("""{"p":"{FilePath}"}""")));

        Assert.Contains("per elemento", ex.Errors[0]);
    }

    [Fact]
    public void Resolve_string_prefers_the_scope_and_never_expands_substituted_values()
    {
        var engine = Engine(("RootDir", "D:"));
        var scope = new Dictionary<string, string> { ["FilePath"] = @"C:\x\{RootDir}.dll" };

        var resolved = engine.Resolve("/s \"{FilePath}\" {RootDir}", scope);

        Assert.Equal("/s \"C:\\x\\{RootDir}.dll\" D:", resolved);
    }

    [Theory]
    [InlineData("FilePath")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("")]
    public void Constructor_rejects_reserved_or_malformed_names(string name)
    {
        Assert.Throws<VariableResolutionException>(() => Engine((name, "v")));
    }

    [Fact]
    public void Constructor_rejects_values_that_contain_tokens()
    {
        var ex = Assert.Throws<VariableResolutionException>(() => Engine(("A", "{B}")));

        Assert.Contains("variables.A", ex.Errors[0]);
    }

    [Fact]
    public void ContainsToken_matches_only_the_listed_names()
    {
        var names = new HashSet<string> { "FilePath" };

        Assert.True(VariableEngine.ContainsToken("a {FilePath} b", names));
        Assert.False(VariableEngine.ContainsToken("a {Other} b {2}", names));
    }
}
