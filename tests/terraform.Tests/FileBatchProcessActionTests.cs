using System.Diagnostics;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class FileBatchProcessActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static System.Text.Json.JsonElement Cfg(object config) => System.Text.Json.JsonSerializer.SerializeToElement(config);

    private Task<ActionResult> Run(FakeProcessRunner runner, object config, bool dryRun = false, CancellationToken cancellationToken = default) =>
        new FileBatchProcessAction(runner).ExecuteAsync(_env.Context(dryRun: dryRun, cancellationToken: cancellationToken), Cfg(config));

    private static string[] Args(FakeProcessRunner runner) => runner.Calls.Select(c => c.Arguments).Order().ToArray();

    [Fact]
    public async Task Runs_the_command_for_each_filtered_file_with_item_tokens()
    {
        var root = _env.Combine("dir with space");
        var a = _env.CreateFile(@"dir with space\EURC_a.dll");
        var b = _env.CreateFile(@"dir with space\EURW_b.dll");
        _env.CreateFile(@"dir with space\other.dll");
        _env.CreateFile(@"dir with space\sub\EURC_c.dll");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new
        {
            searchPaths = new[] { root },
            filter = new { pattern = @"^EUR[CW].*\.dll$", matchType = "Regex", ignoreCase = true },
            command = "regsvr32.exe",
            arguments = "/s \"{FilePath}\""
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { $"/s \"{a}\"", $"/s \"{b}\"" }.Order(), Args(runner));
        Assert.All(runner.Calls, call => Assert.Equal("regsvr32.exe", call.FileName));
    }

    [Fact]
    public async Task Resolves_file_name_and_directory_tokens()
    {
        var root = _env.Combine("d");
        _env.CreateFile(@"d\one.txt");
        var runner = new FakeProcessRunner();

        await Run(runner, new { searchPaths = new[] { root }, command = "tool", arguments = "{FileName}|{Directory}" });

        Assert.Equal($"one.txt|{root}", Assert.Single(runner.Calls).Arguments);
    }

    [Fact]
    public async Task Recurse_controls_whether_subfolders_are_visited()
    {
        var root = _env.Combine("d");
        _env.CreateFile(@"d\top.txt");
        _env.CreateFile(@"d\sub\deep.txt");

        var flat = new FakeProcessRunner();
        await Run(flat, new { searchPaths = new[] { root }, command = "tool", arguments = "{FileName}" });
        Assert.Equal(new[] { "top.txt" }, Args(flat));

        var deep = new FakeProcessRunner();
        await Run(deep, new { searchPaths = new[] { root }, recurse = true, command = "tool", arguments = "{FileName}" });
        Assert.Equal(new[] { "deep.txt", "top.txt" }, Args(deep));
    }

    [Fact]
    public async Task Never_exceeds_max_concurrency()
    {
        for (var i = 0; i < 20; i++)
        {
            _env.CreateFile($@"d\f{i}.txt");
        }

        var runner = new FakeProcessRunner();
        var result = await Run(runner, new { searchPaths = new[] { _env.Combine("d") }, maxConcurrency = 3, command = "tool", arguments = "{FilePath}" });

        Assert.True(result.Success);
        Assert.Equal(20, runner.Calls.Count);
        Assert.InRange(runner.MaxObserved, 2, 3);
    }

    [Fact]
    public async Task Processes_every_file_and_fails_the_step_when_one_fails()
    {
        for (var i = 0; i < 4; i++)
        {
            _env.CreateFile($@"d\ok{i}.txt");
        }

        _env.CreateFile(@"d\bad.txt");
        var runner = new FakeProcessRunner((_, args, _) =>
            Task.FromResult(args.Contains("bad") ? new ProcessResult(5, "", "access denied\nmore") : new ProcessResult(0, "", "")));

        var result = await Run(runner, new { searchPaths = new[] { _env.Combine("d") }, command = "tool", arguments = "{FilePath}" });

        Assert.False(result.Success);
        Assert.Contains("1 falliti su 5", result.Message);
        Assert.Equal(5, runner.Calls.Count);
    }

    [Fact]
    public async Task A_runner_exception_counts_as_a_failure_without_stopping_the_batch()
    {
        _env.CreateFile(@"d\a.txt");
        _env.CreateFile(@"d\b.txt");
        var runner = new FakeProcessRunner((_, _, _) => throw new InvalidOperationException("impossibile avviare"));

        var result = await Run(runner, new { searchPaths = new[] { _env.Combine("d") }, command = "missing", arguments = "{FilePath}" });

        Assert.False(result.Success);
        Assert.Contains("2 falliti su 2", result.Message);
    }

    [Fact]
    public async Task Dry_run_starts_no_process()
    {
        _env.CreateFile(@"d\a.txt");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new { searchPaths = new[] { _env.Combine("d") }, command = "tool", arguments = "{FilePath}" }, dryRun: true);

        Assert.True(result.Success);
        Assert.Empty(runner.Calls);
        Assert.Contains("1 comandi", result.Message);
    }

    [Fact]
    public async Task Dry_run_tolerates_a_root_that_an_earlier_step_would_create()
    {
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new { searchPaths = new[] { _env.Combine("not-yet") }, command = "tool" }, dryRun: true);

        Assert.True(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task No_matching_file_is_a_success()
    {
        _env.CreateFile(@"d\a.txt");
        var runner = new FakeProcessRunner();

        var result = await Run(runner, new
        {
            searchPaths = new[] { _env.Combine("d") },
            filter = new { pattern = "*.dll", matchType = "Glob" },
            command = "tool"
        });

        Assert.True(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Includes_hidden_files()
    {
        var hidden = _env.CreateFile(@"d\hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var runner = new FakeProcessRunner();

        await Run(runner, new { searchPaths = new[] { _env.Combine("d") }, command = "tool", arguments = "{FileName}" });

        Assert.Equal(new[] { "hidden.txt" }, Args(runner));
    }

    [Fact]
    public async Task Does_not_follow_junctions()
    {
        var target = _env.Combine("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "behind-link.txt"), "");
        var root = _env.Combine("root");
        Directory.CreateDirectory(root);
        _env.CreateFile(@"root\real.txt");

        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "link")}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!)
        {
            await mklink.WaitForExitAsync();
            Assert.Equal(0, mklink.ExitCode);
        }

        var runner = new FakeProcessRunner();

        try
        {
            await Run(runner, new { searchPaths = new[] { root }, recurse = true, command = "tool", arguments = "{FileName}" });

            Assert.Equal(new[] { "real.txt" }, Args(runner));
        }
        finally
        {
            // Il cleanup ricorsivo non riesce a rimuovere la junction.
            Directory.Delete(Path.Combine(root, "link"));
        }
    }

    [Fact]
    public async Task Rejects_a_missing_search_path_instead_of_silently_matching_nothing()
    {
        var ex = await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { _env.Combine("nope") }, command = "tool" }));

        Assert.Contains("inesistente", ex.Message);
    }

    [Fact]
    public async Task Rejects_relative_search_paths()
    {
        await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { @"dll" }, command = "tool" }));
    }

    [Fact]
    public async Task Rejects_duplicate_and_nested_roots_only_where_files_would_run_twice()
    {
        _env.CreateFile(@"d\sub\a.txt");
        var parent = _env.Combine("d");
        var child = _env.Combine("d", "sub");

        await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { parent, parent }, command = "tool" }));
        await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { parent, child }, recurse = true, command = "tool" }));

        var flat = await Run(new FakeProcessRunner(), new { searchPaths = new[] { parent, child }, command = "tool" });
        Assert.True(flat.Success);
    }

    [Fact]
    public async Task Rejects_item_tokens_outside_arguments()
    {
        _env.CreateFile(@"d\a.txt");

        await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { _env.Combine("d") }, command = "{FilePath}" }));
    }

    [Fact]
    public async Task Rejects_invalid_concurrency_and_missing_command()
    {
        _env.CreateFile(@"d\a.txt");
        var root = _env.Combine("d");

        await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { root }, maxConcurrency = 0, command = "tool" }));
        await Assert.ThrowsAsync<ActionConfigException>(() =>
            Run(new FakeProcessRunner(), new { searchPaths = new[] { root } }));
    }

    [Fact]
    public async Task Cancellation_stops_the_batch()
    {
        for (var i = 0; i < 5; i++)
        {
            _env.CreateFile($@"d\f{i}.txt");
        }

        var runner = new FakeProcessRunner(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "", "");
        });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(runner, new { searchPaths = new[] { _env.Combine("d") }, command = "tool" }, cancellationToken: cts.Token));
    }

    [Fact]
    public void Declares_the_reserved_tokens_as_item_tokens()
    {
        var action = new FileBatchProcessAction(new FakeProcessRunner());

        Assert.Equal(VariableEngine.ReservedNames.Order(), action.ItemTokens.Order());
    }
}
