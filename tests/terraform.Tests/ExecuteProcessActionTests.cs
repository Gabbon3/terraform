using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class ExecuteProcessActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private Task<ActionResult> Run(FakeProcessRunner runner, object config, bool dryRun = false, CancellationToken cancellationToken = default) =>
        new ExecuteProcessAction(runner).ExecuteAsync(_env.Context(dryRun: dryRun, cancellationToken: cancellationToken), JsonSerializer.SerializeToElement(config));

    private static FakeProcessRunner ExitCodes(Func<string, int> exitCode) =>
        new((file, _, _) => Task.FromResult(new ProcessResult(exitCode(file), "", "")));

    private string Log() => File.ReadAllText(_env.Combine("logs", "test.log"));

    [Fact]
    public async Task Runs_items_in_order_one_at_a_time_with_default_and_per_item_arguments()
    {
        var a = _env.CreateFile(@"i\a.exe");
        var b = _env.CreateFile(@"i\b.exe");
        var c = _env.CreateFile(@"i\c.exe");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new
        {
            defaultArguments = "/install /quiet",
            items = new object[]
            {
                new { path = a },
                new { path = b, arguments = "/s" },
                new { path = c, arguments = "" }
            }
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { (a, "/install /quiet"), (b, "/s"), (c, "") }, runner.Calls.ToArray());
        Assert.Equal(1, runner.MaxObserved);
    }

    [Fact]
    public async Task Built_in_success_codes_accept_reboot_required_and_already_installed()
    {
        var a = _env.CreateFile(@"i\a.exe");
        var b = _env.CreateFile(@"i\b.exe");
        var c = _env.CreateFile(@"i\c.exe");
        var codes = new Dictionary<string, int> { [a] = 0, [b] = 3010, [c] = 1638 };

        var result = await Run(ExitCodes(file => codes[file]), new { items = new object[] { new { path = a }, new { path = b }, new { path = c } } });

        Assert.True(result.Success, result.Message);
        Assert.Contains("riavvio richiesto", Log());
        Assert.Contains("già installato", Log());
    }

    [Fact]
    public async Task A_failing_item_stops_the_sequence()
    {
        var a = _env.CreateFile(@"i\a.exe");
        var b = _env.CreateFile(@"i\b.exe");
        var runner = ExitCodes(file => file == a ? 5 : 0);

        var result = await Run(runner, new { items = new object[] { new { path = a }, new { path = b } } });

        Assert.False(result.Success);
        Assert.Contains("exit code 5", result.Message);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Success_codes_can_be_overridden_globally_and_per_item()
    {
        var a = _env.CreateFile(@"i\a.exe");
        var b = _env.CreateFile(@"i\b.exe");

        var strict = await Run(ExitCodes(_ => 3010), new { defaultSuccessExitCodes = new[] { 0 }, items = new object[] { new { path = a } } });
        var perItem = await Run(ExitCodes(_ => 5), new { items = new object[] { new { path = b, successExitCodes = new[] { 5 } } } });

        Assert.False(strict.Success);
        Assert.True(perItem.Success, perItem.Message);
    }

    [Fact]
    public async Task A_missing_required_file_fails_before_any_process_starts()
    {
        var present = _env.CreateFile(@"i\present.exe");
        var missing = _env.Combine("i", "missing.exe");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new { items = new object[] { new { path = present }, new { path = missing } } });

        Assert.False(result.Success);
        Assert.Contains(missing, result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Ignore_if_not_found_skips_with_a_warning_and_continues()
    {
        var present = _env.CreateFile(@"i\present.exe");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new
        {
            items = new object[] { new { path = _env.Combine("i", "absent.exe"), ignoreIfNotFound = true }, new { path = present } }
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal("1 eseguiti, 1 saltati", result.Message);
        Assert.Single(runner.Calls);
        Assert.Contains("file non trovato, saltato", Log());
    }

    [Fact]
    public async Task A_process_that_cannot_start_fails_the_step()
    {
        var a = _env.CreateFile(@"i\a.exe");
        var runner = new FakeProcessRunner((_, _, _) => throw new InvalidOperationException("impossibile avviare"));

        var result = await Run(runner, new { items = new object[] { new { path = a } } });

        Assert.False(result.Success);
        Assert.Contains("impossibile avviare", result.Message);
    }

    [Fact]
    public async Task Dry_run_starts_nothing_and_tolerates_files_a_previous_step_would_create()
    {
        var present = _env.CreateFile(@"i\present.exe");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new { items = new object[] { new { path = present }, new { path = _env.Combine("i", "later.exe") } } }, dryRun: true);

        Assert.True(result.Success, result.Message);
        Assert.Empty(runner.Calls);
        Assert.Equal("2 da eseguire, 0 saltati", result.Message);
    }

    [Fact]
    public async Task The_manual_steps_warning_is_logged_after_a_successful_step_only()
    {
        var a = _env.CreateFile(@"i\a.exe");

        await Run(ExitCodes(_ => 9), new { manualStepsWarning = "MESSAGGIO-FALLITO", items = new object[] { new { path = a } } });
        await Run(new FakeProcessRunner(), new { manualStepsWarning = "MESSAGGIO-OK", items = new object[] { new { path = a } } });

        Assert.DoesNotContain("MESSAGGIO-FALLITO", Log());
        Assert.Contains("MESSAGGIO-OK", Log());
    }

    [Fact]
    public async Task Rejects_invalid_configurations()
    {
        var a = _env.CreateFile(@"i\a.exe");

        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { items = Array.Empty<object>() }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { items = new object[] { new { path = "setup.exe" } } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { items = new object[] { new { name = "senza path" } } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { items = new object[] { new { path = a, successExitCodes = Array.Empty<int>() } } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { items = new object[] { new { path = a, argumentz = "/s" } } }));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var a = _env.CreateFile(@"i\a.exe");
        var runner = new FakeProcessRunner(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(runner, new { items = new object[] { new { path = a } } }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Pipeline_variables_are_resolved_in_paths_and_arguments()
    {
        var a = _env.CreateFile(@"inst\a.exe");
        var runner = new FakeProcessRunner();
        var pipeline = PipelineLoader.Parse(JsonSerializer.Serialize(new
        {
            variables = new { Dir = _env.Combine("inst"), Mode = "/quiet" },
            pipeline = new object[]
            {
                new { id = "p", type = "EXECUTE-PROCESS", config = new { items = new object[] { new { path = @"{Dir}\a.exe", arguments = "/install {Mode}" } } } }
            }
        }));

        var result = await new PipelineRunner(new ActionResolver([new ExecuteProcessAction(runner)]), _env.Logger).RunAsync(pipeline, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(new[] { (a, "/install /quiet") }, runner.Calls.ToArray());
    }
}
