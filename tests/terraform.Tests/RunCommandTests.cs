using terraform.lib.logger;
using terraform.Pipeline;

namespace terraform.Tests;

public sealed class RunCommandTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private string WritePipeline(object pipeline)
    {
        var path = _env.Combine("pipeline.yaml");
        File.WriteAllText(path, Yaml.Serialize(pipeline));
        return path;
    }

    private object SamplePipeline() => new
    {
        variables = new { Root = _env.Combine("work") },
        pipeline = new object[]
        {
            new { id = "mk", type = "REQUIRED-FOLDERS", config = new { paths = new[] { @"{Root}\out" } } },
            new
            {
                id = "run",
                type = "FILE-BATCH-PROCESS",
                config = new { searchPaths = new[] { _env.Combine("in") }, command = "cmd.exe", arguments = "/c exit 0 & rem {FileName}" }
            }
        }
    };

    private Task<int> Run(string path, bool dryRun = false, CancellationToken cancellationToken = default) =>
        RunCommand.RunPipelineAsync(path, dryRun, _env.Logger, cancellationToken);

    [Fact]
    public async Task Runs_a_pipeline_end_to_end_through_the_real_wiring()
    {
        _env.CreateFile(@"in\a.txt");

        var exit = await Run(WritePipeline(SamplePipeline()));

        Assert.Equal(RunCommand.ExitOk, exit);
        Assert.True(Directory.Exists(_env.Combine("work", "out")));
    }

    [Fact]
    public async Task Dry_run_leaves_the_filesystem_untouched()
    {
        _env.CreateFile(@"in\a.txt");

        var exit = await Run(WritePipeline(SamplePipeline()), dryRun: true);

        Assert.Equal(RunCommand.ExitOk, exit);
        Assert.False(Directory.Exists(_env.Combine("work")));
    }

    [Fact]
    public async Task A_failing_step_gives_exit_code_1()
    {
        var exit = await Run(WritePipeline(new
        {
            pipeline = new object[] { new { id = "x", type = "FILE-BATCH-PROCESS", config = new { searchPaths = new[] { _env.Combine("missing") }, command = "cmd.exe" } } }
        }));

        Assert.Equal(RunCommand.ExitFailed, exit);
    }

    [Fact]
    public async Task Unknown_action_types_and_missing_or_broken_files_give_exit_code_1()
    {
        var unknown = WritePipeline(new { pipeline = new object[] { new { id = "x", type = "no-such-action" } } });
        var broken = _env.CreateFile("broken.yaml", "pipeline: [");

        Assert.Equal(RunCommand.ExitFailed, await Run(unknown));
        Assert.Equal(RunCommand.ExitFailed, await Run(broken));
        Assert.Equal(RunCommand.ExitFailed, await Run(_env.Combine("absent.yaml")));
    }

    [Fact]
    public async Task Cancellation_gives_exit_code_130()
    {
        _env.CreateFile(@"in\a.txt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var exit = await Run(WritePipeline(SamplePipeline()), cancellationToken: cts.Token);

        Assert.Equal(RunCommand.ExitCancelled, exit);
        Assert.False(Directory.Exists(_env.Combine("work")));
    }
}
