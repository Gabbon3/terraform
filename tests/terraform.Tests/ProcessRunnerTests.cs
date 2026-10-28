using System.Diagnostics;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new();

    private Task<ProcessResult> Cmd(string arguments, CancellationToken cancellationToken = default) =>
        _runner.RunAsync("cmd.exe", arguments, cancellationToken);

    [Fact]
    public async Task Returns_exit_code_and_output()
    {
        var failed = await Cmd("/c exit 3");
        var echoed = await Cmd("/c echo hello");

        Assert.Equal(3, failed.ExitCode);
        Assert.Contains("hello", echoed.StandardOutput);
    }

    [Fact]
    public async Task Captures_standard_error_separately()
    {
        var result = await Cmd("/c echo oops 1>&2");

        Assert.Contains("oops", result.StandardError);
        Assert.DoesNotContain("oops", result.StandardOutput);
    }

    [Fact]
    public async Task Large_output_neither_deadlocks_nor_exceeds_the_tail_limit()
    {
        var result = await Cmd("/c \"for /l %i in (1,1,20000) do @echo line %i\"");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutput.Length <= 16 * 1024);
        Assert.Contains("line 20000", result.StandardOutput);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cmd("/c ping -n 30 127.0.0.1 >nul", cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_missing_executable_gives_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _runner.RunAsync("definitely-not-an-executable.exe", "", CancellationToken.None));

        Assert.Contains("definitely-not-an-executable.exe", ex.Message);
    }
}
