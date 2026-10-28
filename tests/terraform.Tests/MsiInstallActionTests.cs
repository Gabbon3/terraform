using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class MsiInstallActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private Task<ActionResult> Run(FakeProcessRunner runner, object config, bool dryRun = false) =>
        new MsiInstallAction(runner).ExecuteAsync(_env.Context(dryRun: dryRun), JsonSerializer.SerializeToElement(config));

    private static FakeProcessRunner Exit(int code) => new((_, _, _) => Task.FromResult(new ProcessResult(code, "", "")));

    [Fact]
    public async Task Installs_each_package_with_msiexec_and_the_default_arguments_in_order()
    {
        var a = _env.CreateFile(@"m\a.msi");
        var b = _env.CreateFile(@"m\b.MSI");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new { paths = new[] { a, b } });

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { ("msiexec.exe", $"/i \"{a}\" /qn /norestart"), ("msiexec.exe", $"/i \"{b}\" /qn /norestart") }, runner.Calls.ToArray());
        Assert.Equal(1, runner.MaxObserved);
    }

    [Fact]
    public async Task Custom_arguments_get_the_current_package_in_place_of_the_token()
    {
        var a = _env.CreateFile(@"m\a.msi");
        var runner = new FakeProcessRunner();

        await Run(runner, new { paths = new[] { a }, msiArguments = "/i \"{MsiPath}\" /qb /l*v \"C:\\logs\\a.log\"" });

        Assert.Equal($"/i \"{a}\" /qb /l*v \"C:\\logs\\a.log\"", Assert.Single(runner.Calls).Arguments);
    }

    [Fact]
    public async Task Default_success_codes_accept_reboot_required_but_not_already_installed()
    {
        var a = _env.CreateFile(@"m\a.msi");

        var reboot = await Run(Exit(3010), new { paths = new[] { a } });
        var already = await Run(Exit(1638), new { paths = new[] { a } });
        var custom = await Run(Exit(1638), new { paths = new[] { a }, successExitCodes = new[] { 0, 1638 } });

        Assert.True(reboot.Success);
        Assert.False(already.Success);
        Assert.True(custom.Success);
    }

    [Fact]
    public async Task A_failing_package_stops_the_sequence()
    {
        var a = _env.CreateFile(@"m\a.msi");
        var b = _env.CreateFile(@"m\b.msi");
        var runner = Exit(1603);

        var result = await Run(runner, new { paths = new[] { a, b } });

        Assert.False(result.Success);
        Assert.Contains("exit code 1603", result.Message);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Missing_files_fail_up_front_unless_ignore_if_not_found_is_set()
    {
        var present = _env.CreateFile(@"m\present.msi");
        var absent = _env.Combine("m", "absent.msi");
        var strict = new FakeProcessRunner();
        var lenient = new FakeProcessRunner();

        var failed = await Run(strict, new { paths = new[] { present, absent } });
        var skipped = await Run(lenient, new { paths = new[] { absent, present }, ignoreIfNotFound = true });

        Assert.False(failed.Success);
        Assert.Empty(strict.Calls);
        Assert.True(skipped.Success, skipped.Message);
        Assert.Single(lenient.Calls);
    }

    [Fact]
    public async Task Dry_run_starts_no_process()
    {
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new { paths = new[] { _env.CreateFile(@"m\a.msi"), _env.Combine("m", "later.msi") } }, dryRun: true);

        Assert.True(result.Success, result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Rejects_invalid_configurations()
    {
        var a = _env.CreateFile(@"m\a.msi");
        var exe = _env.CreateFile(@"m\setup.exe");

        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { paths = Array.Empty<string>() }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { paths = new[] { "a.msi" } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { paths = new[] { exe } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { paths = new[] { a }, msiArguments = "/qn" }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { paths = new[] { @"C:\{MsiPath}.msi" } }));
        await Assert.ThrowsAsync<ActionConfigException>(() => Run(new FakeProcessRunner(), new { paths = new[] { a }, successExitCodes = Array.Empty<int>() }));
    }

    [Fact]
    public void Declares_msi_path_as_an_item_token()
    {
        Assert.Equal(new[] { "MsiPath" }, new MsiInstallAction(new FakeProcessRunner()).ItemTokens);
    }

    [Fact]
    public async Task Through_the_runner_global_variables_resolve_and_msi_path_survives_the_pre_flight()
    {
        var a = _env.CreateFile(@"inst\a.msi");
        var runner = new FakeProcessRunner();
        var pipeline = PipelineLoader.Parse(JsonSerializer.Serialize(new
        {
            variables = new { Dir = _env.Combine("inst"), Level = "/qb" },
            pipeline = new object[]
            {
                new { id = "m", type = "MSI-INSTALL", config = new { paths = new[] { @"{Dir}\a.msi" }, msiArguments = "/i \"{MsiPath}\" {Level}" } }
            }
        }));

        var result = await new PipelineRunner(new ActionResolver([new MsiInstallAction(runner)]), _env.Logger).RunAsync(pipeline, dryRun: false);

        Assert.True(result.Success, string.Join("; ", result.ValidationErrors));
        Assert.Equal(new[] { ("msiexec.exe", $"/i \"{a}\" /qb") }, runner.Calls.ToArray());
    }

    [Fact]
    public async Task A_variable_named_msi_path_is_rejected_before_anything_runs()
    {
        var a = _env.CreateFile(@"inst\a.msi");
        var runner = new FakeProcessRunner();
        var pipeline = PipelineLoader.Parse(JsonSerializer.Serialize(new
        {
            variables = new { MsiPath = a },
            pipeline = new object[] { new { id = "m", type = "MSI-INSTALL", config = new { paths = new[] { a } } } }
        }));

        var result = await new PipelineRunner(new ActionResolver([new MsiInstallAction(runner)]), _env.Logger).RunAsync(pipeline, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains("MsiPath", Assert.Single(result.ValidationErrors));
        Assert.Empty(runner.Calls);
    }
}
