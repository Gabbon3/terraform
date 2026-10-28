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
}
