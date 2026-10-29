using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.engine.actions;

namespace terraform.Tests;

public sealed class EnsureDirectoryActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly EnsureDirectoryAction _action = new();

    public void Dispose() => _env.Dispose();

    private Task<ActionResult> Run(object config, bool dryRun = false) =>
        _action.ExecuteAsync(_env.Context(dryRun: dryRun), JsonSerializer.SerializeToElement(config));

    [Fact]
    public async Task Creates_missing_directories_including_parents_and_is_idempotent()
    {
        var deep = _env.Combine("a", "b", "c");

        var first = await Run(new { paths = new[] { deep } });
        var second = await Run(new { paths = new[] { deep } });

        Assert.True(first.Success);
        Assert.True(Directory.Exists(deep));
        Assert.Contains("1 create", first.Message);
        Assert.Contains("0 create, 1 già presenti", second.Message);
    }

    [Fact]
    public async Task Dry_run_creates_nothing()
    {
        var path = _env.Combine("planned");

        var result = await Run(new { paths = new[] { path } }, dryRun: true);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(path));
        Assert.Contains("1 da creare", result.Message);
    }

    [Fact]
    public async Task Rejects_relative_paths_and_empty_lists()
    {
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new { paths = new[] { @"relative\dir" } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new { paths = new[] { @"\dll" } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new { paths = Array.Empty<string>() }));
    }

    [Fact]
    public async Task A_file_with_the_same_name_fails_before_anything_is_created()
    {
        var file = _env.CreateFile("taken");
        var other = _env.Combine("other");

        var result = await Run(new { paths = new[] { other, file } });

        Assert.False(result.Success);
        Assert.False(Directory.Exists(other));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    [InlineData(-1, true, true)]
    public async Task Replicates_only_directories_up_to_the_requested_depth(int depth, bool firstLevel, bool secondLevel)
    {
        var source = _env.Combine("source");
        var target = _env.Combine("target");
        _env.CreateFile(@"source\child\grandchild\file.txt");
        _env.CreateFile(@"source\root.txt");
        Directory.CreateDirectory(_env.Combine("source", "child", "grandchild", "third"));
        Directory.CreateDirectory(_env.Combine("source", "empty"));

        var config = new { paths = new[] { new { path = target, source, depth } } };
        var result = await Run(config);
        var again = await Run(config);

        Assert.True(result.Success);
        Assert.True(again.Success);
        Assert.True(Directory.Exists(target));
        Assert.Equal(firstLevel, Directory.Exists(Path.Combine(target, "child")));
        Assert.Equal(firstLevel, Directory.Exists(Path.Combine(target, "empty")));
        Assert.Equal(secondLevel, Directory.Exists(Path.Combine(target, "child", "grandchild")));
        Assert.Equal(depth == -1, Directory.Exists(Path.Combine(target, "child", "grandchild", "third")));
        Assert.Empty(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories));
        Assert.StartsWith("0 create", again.Message);
    }

    [Fact]
    public async Task Tree_dry_run_reads_source_without_creating_targets()
    {
        var source = _env.Combine("source");
        _env.CreateFile(@"source\child\grandchild\file.txt");
        var target = _env.Combine("target");

        var result = await Run(new { paths = new[] { new { path = target, source, depth = -1 } } }, dryRun: true);

        Assert.True(result.Success);
        Assert.Contains("3 da creare", result.Message);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Object_entries_default_to_depth_zero_without_reading_source()
    {
        var target = _env.Combine("target");

        var result = await Run(new { paths = new[] { new { path = target, source = _env.Combine("absent") } } });

        Assert.True(result.Success);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Validates_depth_source_and_unknown_fields_without_io()
    {
        var target = _env.Combine("target");
        Assert.Throws<ActionConfigException>(() => _action.Validate(JsonSerializer.SerializeToElement(
            new { paths = new[] { new { path = target, depth = -2 } } })));
        Assert.Throws<ActionConfigException>(() => _action.Validate(JsonSerializer.SerializeToElement(
            new { paths = new[] { new { path = target, depth = 1 } } })));
        Assert.Throws<ActionConfigException>(() => _action.Validate(JsonSerializer.SerializeToElement(
            new { paths = new[] { new { path = target, source = "relative", depth = 1 } } })));
        Assert.Throws<ActionConfigException>(() => _action.Validate(JsonSerializer.SerializeToElement(
            new { paths = new[] { new { path = target, typo = 1 } } })));
        _action.Validate(JsonSerializer.SerializeToElement(
            new { paths = new[] { new { path = target, source = _env.Combine("absent"), depth = -1 } } }));
    }

    [Fact]
    public async Task Conflicting_child_file_prevents_all_creation()
    {
        Directory.CreateDirectory(_env.Combine("source", "child"));
        _env.CreateFile(@"target\child");
        var other = _env.Combine("other");

        var result = await Run(new
        {
            paths = new object[] { other, new { path = _env.Combine("target"), source = _env.Combine("source"), depth = 1 } }
        });

        Assert.False(result.Success);
        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public async Task Missing_source_prevents_all_creation()
    {
        var target = _env.Combine("target");
        var other = _env.Combine("other");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Run(new
        {
            paths = new object[] { other, new { path = target, source = _env.Combine("absent"), depth = 1 } }
        }));

        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(other));
    }
}
