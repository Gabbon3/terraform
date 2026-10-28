using System.Text;
using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.engine.actions;

namespace terraform.Tests;

public sealed class EditHostsActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly EditHostsAction _action = new();

    public void Dispose() => _env.Dispose();

    private Task<ActionResult> Run(string? path, object[] entries, bool dryRun = false) =>
        _action.ExecuteAsync(_env.Context(dryRun: dryRun), JsonSerializer.SerializeToElement(new { path, entries }));

    private static object Entry(string ip, string host) => new { ip, host };

    [Fact]
    public async Task Applies_the_entries_and_a_second_run_changes_nothing()
    {
        var path = _env.CreateFile("hosts", "127.0.0.1 localhost\r\n");

        var first = await Run(path, [Entry("10.0.0.5", "server1")]);
        var afterFirst = await File.ReadAllBytesAsync(path);
        var second = await Run(path, [Entry("10.0.0.5", "server1")]);

        Assert.True(first.Success);
        Assert.Equal("127.0.0.1 localhost\r\n10.0.0.5\tserver1\r\n", Encoding.UTF8.GetString(afterFirst));
        Assert.Equal("hosts già allineato", second.Message);
        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Dry_run_does_not_write()
    {
        var path = _env.CreateFile("hosts", "127.0.0.1 localhost\r\n");
        var before = await File.ReadAllBytesAsync(path);

        var result = await Run(path, [Entry("10.0.0.5", "server1")], dryRun: true);

        Assert.True(result.Success);
        Assert.Contains("1 modifiche", result.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Preserves_a_utf8_bom()
    {
        var path = _env.Combine("hosts");
        byte[] content = [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes("# perché\r\n")];
        await File.WriteAllBytesAsync(path, content);

        await Run(path, [Entry("10.0.0.5", "server1")]);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("# perché\r\n10.0.0.5\tserver1\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Preserves_the_bytes_of_an_ansi_file()
    {
        var path = _env.Combine("hosts");
        byte[] ansi = [.. Encoding.ASCII.GetBytes("# perch"), 0xE9, .. Encoding.ASCII.GetBytes("\r\n")];
        await File.WriteAllBytesAsync(path, ansi);

        await Run(path, [Entry("10.0.0.5", "server1")]);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(ansi, bytes[..ansi.Length]);
    }

    [Fact]
    public async Task A_missing_file_is_a_failure_and_is_not_created()
    {
        var path = _env.Combine("absent");

        var result = await Run(path, [Entry("10.0.0.5", "server1")]);

        Assert.False(result.Success);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("999.1.1.1", "server1")]
    [InlineData("127.1", "server1")]
    [InlineData("not-an-ip", "server1")]
    [InlineData("10.0.0.5", "bad host")]
    [InlineData("10.0.0.5", "bad#host")]
    [InlineData("10.0.0.5", "")]
    public async Task Rejects_invalid_entries(string ip, string host)
    {
        var path = _env.CreateFile("hosts", "");

        await Assert.ThrowsAsync<ActionConfigException>(() => Run(path, [Entry(ip, host)]));
    }

    [Fact]
    public async Task Rejects_empty_duplicate_and_relative_configurations()
    {
        var path = _env.CreateFile("hosts", "");

        await Assert.ThrowsAsync<ActionConfigException>(() => Run(path, []));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(path, [Entry("10.0.0.1", "a"), Entry("10.0.0.2", "A")]));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run("hosts", [Entry("10.0.0.1", "a")]));
    }
}
