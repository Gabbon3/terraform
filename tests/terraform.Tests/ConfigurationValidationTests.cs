using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.logger;
using terraform.Pipeline;

namespace terraform.Tests;

/// <summary>Verifica il preflight condiviso e il comando statico, senza eseguire processi nativi o query remote.</summary>
public sealed class ConfigurationValidationTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    /// <summary>Serializza lo stesso formato YAML letto dalla CLI.</summary>
    private static PipelineDefinition Pipeline(params object[] steps) =>
        PipelineLoader.Parse(Yaml.Serialize(new { pipeline = steps }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_late_configs_prevent_even_the_first_step_from_creating_a_directory(bool dryRun)
    {
        var target = _env.Combine("must-not-exist");
        var native = new FakeProcessRunner();
        var pipeline = Pipeline(
            new { id = "first", type = "REQUIRED-FOLDERS", config = new { paths = new[] { target } } },
            new { id = "late", type = "MSI-INSTALL", config = new { paths = new[] { @"D:\package.exe" } } },
            new { id = "copy", type = "ROBOCOPY", config = new { source = @"D:\data", destination = @"D:\data\backup" } });

        var result = await new PipelineRunner(new ActionResolver(ActionCatalog.Create(native)), _env.Logger).RunAsync(pipeline, dryRun);

        Assert.False(result.Success);
        Assert.Empty(result.Steps);
        Assert.Equal(2, result.ValidationErrors.Count);
        Assert.Contains(result.ValidationErrors, e => e.Contains("late.config") && e.Contains("paths[0]"));
        Assert.Contains(result.ValidationErrors, e => e.Contains("copy.config") && e.Contains("sovrappongono"));
        Assert.False(Directory.Exists(target));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Every_catalog_action_validates_required_fields_and_rejects_unknown_properties()
    {
        var native = new FakeProcessRunner();
        var actions = ActionCatalog.Create(native);
        foreach (var action in actions)
        {
            Assert.Throws<ActionConfigException>(() => action.Validate(Json.Element("{}")));
            var error = Assert.Throws<ActionConfigException>(() => action.Validate(Json.Element("""{"typo": 1}""")));
            Assert.Contains("typo", error.Message);
        }
        var errors = PipelineRunner.Validate(Pipeline(actions.Select(action =>
            (object)new { id = action.ActionType, type = action.ActionType }).ToArray()), new ActionResolver(actions));

        Assert.Equal(actions.Count, errors.Count);
        Assert.All(errors, error => Assert.Contains(".config:", error));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task Validate_accepts_future_artifacts_and_unreachable_servers_without_creating_anything()
    {
        var target = _env.Combine("future");
        var pipeline = new
        {
            variables = new { Root = target, Server = "does-not-exist.invalid" },
            pipeline = new object[]
            {
                new { id = "dirs", type = "REQUIRED-FOLDERS", config = new { paths = new[] { "{Root}" } } },
                new { id = "exe", type = "EXECUTE-PROCESS", config = new { items = new[] { new { path = @"{Root}\setup.exe" } } } },
                new { id = "msi", type = "MSI-INSTALL", config = new { paths = new[] { @"{Root}\setup.msi" }, msiArguments = "/i \"{MsiPath}\" /qn" } },
                new { id = "batch", type = "FILE-BATCH-PROCESS", config = new { searchPaths = new[] { "{Root}" }, command = "regsvr32.exe", arguments = "\"{FilePath}\"" } },
                new { id = "hosts", type = "HOSTS", config = new { path = @"{Root}\hosts", entries = new[] { new { ip = "127.0.0.1", host = "local" } } } },
                new { id = "copy", type = "ROBOCOPY", config = new { source = @"{Root}\source", destination = @"{Root}\target" } },
                new { id = "svc", type = "CREATE-SERVICES", config = new { sourceComputerName = "{Server}", credentials = new Dictionary<string, string>() } },
                new { id = "task", type = "CREATE-TASKS", config = new { sourceComputerName = "{Server}", credentials = new Dictionary<string, string>() } },
                new { id = "disabled", type = "future-action", enabled = false, config = new { invalid = "{Unknown}" } }
            }
        };
        var path = _env.CreateFile("pipeline.yaml", Yaml.Serialize(pipeline));

        Assert.Equal(RunCommand.ExitOk, await ValidateCommand.ValidatePipelineAsync(path));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("searchPaths: ['D:\\future']\ncommand: tool\nfilter:\n  pattern: '['\n  matchType: Regex", "filter.pattern")]
    [InlineData("searchPaths: ['D:\\future']\ncommand: tool\nfilter:\n  minSize: bad", "filter.minSize")]
    [InlineData("searchPaths: ['D:\\future']\ncommand: tool\nmaxConcurrency: 0", "maxConcurrency")]
    public void Preflight_checks_filters_and_constraints_without_enumerating(string config, string field)
    {
        var indentedConfig = string.Join(Environment.NewLine, config.Split('\n').Select(line => $"      {line}"));
        var pipeline = PipelineLoader.Parse($"pipeline:\n  - id: batch\n    type: FILE-BATCH-PROCESS\n    config:\n{indentedConfig}");
        var errors = PipelineRunner.Validate(pipeline, new ActionResolver(ActionCatalog.Create(new FakeProcessRunner())));

        Assert.Contains(field, Assert.Single(errors));
    }

    [Fact]
    public async Task Validate_returns_direct_exit_codes_for_errors_and_cancellation()
    {
                Assert.Equal(RunCommand.ExitFailed, await ValidateCommand.ValidatePipelineAsync(_env.Combine("absent.yaml")));
                Assert.Equal(RunCommand.ExitFailed, await ValidateCommand.ValidatePipelineAsync(_env.CreateFile("bad.yaml", "pipeline: [")));
                var invalid = _env.CreateFile("invalid.yaml", """
                        pipeline:
                            - id: x
                                type: REQUIRED-FOLDERS
                                config: {}
                        """);
        Assert.Equal(RunCommand.ExitFailed, await ValidateCommand.ValidatePipelineAsync(invalid));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(RunCommand.ExitCancelled, await ValidateCommand.ValidatePipelineAsync(invalid, cts.Token));
    }

    [Fact]
    public async Task Parallel_warnings_are_counted_per_step_without_changing_success_or_leaking_to_the_next_step()
    {
        const int warningCount = 24;
        var warned = new FakeAction("warn", (context, _) =>
        {
            Parallel.For(0, warningCount, i => context.Logger.Write($"warning {i}", LogLevel.Warn, isParallel: true));
            return Task.FromResult(new ActionResult(true));
        });
        var clean = new FakeAction("clean");
        var pipeline = Pipeline(new { id = "warned", type = "warn" }, new { id = "clean", type = "clean" });

        var result = await new PipelineRunner(new ActionResolver([warned, clean]), _env.Logger).RunAsync(pipeline, false);

        Assert.True(result.Success);
        Assert.All(result.Steps, step => Assert.Equal(StepStatus.Succeeded, step.Status));
        Assert.Equal(warningCount, result.Steps[0].WarningCount);
        Assert.Equal(0, result.Steps[1].WarningCount);
        var log = File.ReadAllText(_env.Combine("logs", "test.log"));
        Assert.Contains($"warned - warned ({warningCount} avvisi)", log);
    }
}
