using System.Text.Json;
using terraform.lib.engine;

namespace terraform.Tests;

public class PipelineLoaderTests
{
    [Fact]
    public void Parse_reads_yaml_and_enables_steps_by_default()
    {
        var pipeline = PipelineLoader.Parse("""
            # commento
            variables:
              A: '1'
            pipeline:
              - id: s1
                type: t
            """);

        Assert.Equal("1", pipeline.Variables!["A"]);
        var step = Assert.Single(pipeline.Steps!)!;
        Assert.True(step.Enabled);
        Assert.Equal(JsonValueKind.Undefined, step.Config.ValueKind);
    }

    [Fact]
    public void Parse_rejects_unknown_properties_so_a_typo_cannot_leave_a_step_enabled()
    {
        var ex = Assert.Throws<PipelineLoadException>(() =>
            PipelineLoader.Parse("""
                pipeline:
                  - id: s
                    type: t
                    enable: false
                """));

        Assert.Contains("enable", ex.Message);
    }

    [Fact]
    public void Parse_reports_the_line_of_a_syntax_error()
    {
        var ex = Assert.Throws<PipelineLoadException>(() =>
            PipelineLoader.Parse("pipeline:\n  - id: bad: value\n", "demo.yaml"));

        Assert.Contains("demo.yaml", ex.Message);
        Assert.Contains("YAML non valido", ex.Message);
        Assert.Contains("riga 2", ex.Message);
    }

    [Fact]
    public void Parse_rejects_non_string_variables()
    {
                Assert.Throws<PipelineLoadException>(() => PipelineLoader.Parse("""
                        variables:
                            Port: 1433
                        pipeline: []
                        """));
    }

    [Fact]
    public async Task LoadAsync_reports_a_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "terraform-tests-missing", "p.yaml");

        var ex = await Assert.ThrowsAsync<PipelineLoadException>(() => PipelineLoader.LoadAsync(path));

        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public async Task LoadAsync_rejects_json_files_without_reading_them()
    {
        var path = Path.Combine(Path.GetTempPath(), "pipeline.json");

        var ex = await Assert.ThrowsAsync<PipelineLoadException>(() => PipelineLoader.LoadAsync(path));

        Assert.Contains(".yaml o .yml", ex.Message);
    }
}

public class ActionResolverTests
{
    [Fact]
    public void Resolve_ignores_case()
    {
        var action = new FakeAction("ensure-directory");

        Assert.Same(action, new ActionResolver([action]).Resolve("Ensure-Directory"));
    }

    [Fact]
    public void Constructor_rejects_the_same_type_registered_twice()
    {
        Assert.Throws<InvalidOperationException>(() => new ActionResolver([new FakeAction("a"), new FakeAction("A")]));
    }

    [Fact]
    public void Resolve_lists_the_available_types_for_an_unknown_one()
    {
        var resolver = new ActionResolver([new FakeAction("b"), new FakeAction("a")]);

        var ex = Assert.Throws<KeyNotFoundException>(() => resolver.Resolve("zzz"));

        Assert.Contains("a, b", ex.Message);
    }
}
