using terraform.lib.engine;

namespace terraform.Tests;

public sealed class PipelineRunnerTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private PipelineRunner Runner(params IAction[] actions) => new(new ActionResolver(actions), _env.Logger);

    private static StepStatus[] Statuses(PipelineRunResult result) => result.Steps.Select(s => s.Status).ToArray();

    [Fact]
    public async Task Runs_enabled_steps_in_order_with_the_resolved_config()
    {
        var first = new FakeAction("first");
        var second = new FakeAction("second");
        var pipeline = PipelineLoader.Parse("variables:\n  RootDir: 'D:\\data'\npipeline:\n  - id: a\n    type: first\n    config:\n      path: '{RootDir}\\one'\n  - id: b\n    type: second\n    config:\n      path: '{RootDir}\\two'");

        var result = await Runner(first, second).RunAsync(pipeline, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(@"D:\data\one", Assert.Single(first.Calls).Config.GetProperty("path").GetString());
        Assert.Equal(@"D:\data\two", Assert.Single(second.Calls).Config.GetProperty("path").GetString());
        Assert.Equal(new[] { StepStatus.Succeeded, StepStatus.Succeeded }, Statuses(result));
    }

    [Fact]
    public async Task Skips_disabled_steps_even_when_their_type_is_unknown()
    {
        var action = new FakeAction("known");
        var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: future-action\n    enabled: false\n  - id: b\n    type: known");

        var result = await Runner(action).RunAsync(pipeline, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(new[] { StepStatus.Skipped, StepStatus.Succeeded }, Statuses(result));
        Assert.Single(action.Calls);
    }

    [Fact]
    public async Task Stops_at_the_first_failure_and_does_not_run_later_steps()
    {
        var failing = new FakeAction("fail", (_, _) => Task.FromResult(new ActionResult(false, "boom")));
        var later = new FakeAction("later");
        var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: fail\n  - id: b\n    type: later");

        var result = await Runner(failing, later).RunAsync(pipeline, dryRun: false);

        Assert.False(result.Success);
        Assert.Equal(new[] { StepStatus.Failed, StepStatus.NotRun }, Statuses(result));
        Assert.Equal("boom", result.Steps[0].Message);
        Assert.Empty(later.Calls);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_failed_step()
    {
        var crashing = new FakeAction("crash", (_, _) => throw new InvalidOperationException("kaput"));
            var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: crash");

        var result = await Runner(crashing).RunAsync(pipeline, dryRun: false);

        Assert.Equal(StepStatus.Failed, result.Steps[0].Status);
        Assert.Equal("InvalidOperationException: kaput", result.Steps[0].Message);
    }

    [Fact]
    public async Task A_config_error_keeps_its_message_without_the_exception_type()
    {
        var action = new FakeAction("bad", (_, _) => throw new ActionConfigException("paths: valore mancante"));
            var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: bad");

        var result = await Runner(action).RunAsync(pipeline, dryRun: false);

        Assert.Equal("paths: valore mancante", result.Steps[0].Message);
    }

    [Fact]
    public async Task An_invalid_pipeline_runs_nothing_and_reports_every_problem()
    {
        var action = new FakeAction("known");
        var pipeline = PipelineLoader.Parse("variables:\n  A: '1'\npipeline:\n  - id: dup\n    type: known\n  - id: dup\n    type: known\n    config:\n      p: '{Nope}'\n  - id: x\n    type: unknown\n  - type: known");

        var result = await Runner(action).RunAsync(pipeline, dryRun: false);

        Assert.False(result.Success);
        Assert.Empty(action.Calls);
        Assert.Empty(result.Steps);
        Assert.Contains(result.ValidationErrors, e => e.Contains("id duplicato 'dup'"));
        Assert.Contains(result.ValidationErrors, e => e.Contains("{Nope}"));
        Assert.Contains(result.ValidationErrors, e => e.Contains("tipo di azione sconosciuto 'unknown'"));
        Assert.Contains(result.ValidationErrors, e => e.Contains("'id' mancante"));
    }

    [Fact]
    public async Task An_empty_pipeline_is_invalid()
    {
        var result = await Runner().RunAsync(PipelineLoader.Parse("pipeline: []"), dryRun: false);

        Assert.False(result.Success);
        Assert.Contains(result.ValidationErrors, e => e.Contains("non contiene step"));
    }

    [Fact]
    public async Task Cancellation_stops_the_pipeline_and_is_not_reported_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var waiting = new FakeAction("wait", async (context, _) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new ActionResult(true);
        });
        var later = new FakeAction("later");
        var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: wait\n  - id: b\n    type: later");

        var run = Runner(waiting, later).RunAsync(pipeline, dryRun: false, cts.Token);
        await started.Task;
        cts.Cancel();
        var result = await run;

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Equal(new[] { StepStatus.Cancelled, StepStatus.NotRun }, Statuses(result));
        Assert.Empty(later.Calls);
    }

    [Fact]
    public async Task The_dry_run_flag_and_the_declared_per_file_tokens_reach_the_action()
    {
        var action = new FakeAction("batch", null, "FilePath");
        var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: batch\n    config:\n      args: '/s {FilePath}'");

        var result = await Runner(action).RunAsync(pipeline, dryRun: true);

        Assert.True(result.Success);
        var call = Assert.Single(action.Calls);
        Assert.True(call.DryRun);
        Assert.Equal("/s {FilePath}", call.Config.GetProperty("args").GetString());
    }

    [Fact]
    public async Task A_per_file_token_in_an_action_that_does_not_declare_it_is_a_preflight_error()
    {
        var action = new FakeAction("plain");
        var pipeline = PipelineLoader.Parse("pipeline:\n  - id: a\n    type: plain\n    config:\n      path: '{FilePath}'");

        var result = await Runner(action).RunAsync(pipeline, dryRun: false);

        Assert.False(result.Success);
        Assert.Empty(action.Calls);
        Assert.Contains(result.ValidationErrors, e => e.Contains("{FilePath}"));
    }

    [Fact]
    public async Task Variable_errors_are_reported_together_with_step_errors()
    {
        var pipeline = PipelineLoader.Parse("variables:\n  FileName: x\npipeline:\n  - id: a\n    type: missing");

        var result = await Runner().RunAsync(pipeline, dryRun: false);

        Assert.Contains(result.ValidationErrors, e => e.Contains("variables.FileName"));
    }
}
