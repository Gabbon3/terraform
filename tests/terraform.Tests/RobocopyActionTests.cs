using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class RobocopyActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static RobocopyConfig Config(string? source = @"D:\src", string? destination = @"E:\dst", bool? mirror = null, string? extra = null,
        int? threads = null, int? retries = null, List<string>? files = null, List<string>? xd = null, List<string>? xf = null, string? copyOptions = null) => new()
    {
        Source = source,
        Destination = destination,
        Mirror = mirror,
        ExtraArguments = extra,
        CopyOptions = copyOptions,
        Threads = threads,
        Retries = retries,
        Files = files,
        ExcludeDirs = xd,
        ExcludeFiles = xf
    };

    // Disco simulato: per default la sorgente è una cartella e la destinazione non esiste.
    private static RobocopyCommand Build(RobocopyConfig config, bool dryRun = false, bool sourceIsFile = false, bool destinationExists = false) =>
        RobocopyAction.Build(config, dryRun, _ => sourceIsFile, _ => destinationExists);

    [Fact]
    public void Defaults_for_a_folder_match_the_migrator()
    {
        var command = Build(Config());

        Assert.Equal("\"D:\\src\" \"E:\\dst\" /MIR /MT:16 /COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP", command.Arguments);
        Assert.True(command.DeletesExtraFiles);
    }

    [Fact]
    public void Adds_secfix_only_when_the_destination_already_exists()
    {
        Assert.EndsWith(" /NP /SECFIX", Build(Config(), destinationExists: true).Arguments);
        Assert.DoesNotContain("/SECFIX", Build(Config()).Arguments);
    }

    [Fact]
    public void A_file_source_copies_just_that_file_without_mirror_or_threads()
    {
        var command = Build(Config(source: @"D:\src\a.dll", destination: @"E:\dst\a.dll"), sourceIsFile: true, destinationExists: true);

        Assert.Equal("\"D:\\src\" \"E:\\dst\" \"a.dll\" /COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP /SECFIX", command.Arguments);
        Assert.False(command.DeletesExtraFiles);
    }

    [Fact]
    public void A_file_source_rejects_folder_only_options_and_a_destination_without_folder()
    {
        var file = Config(source: @"D:\src\a.dll", destination: @"E:\dst\a.dll", xd: ["temp"]);

        Assert.Throws<ActionConfigException>(() => Build(file, sourceIsFile: true));
        Assert.Throws<ActionConfigException>(() => Build(Config(source: @"D:\src\a.dll", destination: @"E:\"), sourceIsFile: true));
    }

    [Fact]
    public void Dry_run_appends_list_only()
    {
        Assert.EndsWith(" /L", Build(Config(), dryRun: true).Arguments);
        Assert.DoesNotContain("/L", Build(Config()).Arguments);
    }

    [Fact]
    public void Overrides_files_excludes_copy_options_and_extra_arguments_keep_their_order()
    {
        var command = Build(
            Config(copyOptions: "/COPY:DAT", extra: " /XA:H ", threads: 8, retries: 3, files: ["*.dll", "a b.txt"], xd: ["temp", @"D:\src\cache"], xf: ["*.tmp"]),
            destinationExists: true);

        Assert.Equal(
            "\"D:\\src\" \"E:\\dst\" \"*.dll\" \"a b.txt\" /MIR /MT:8 /COPY:DAT /R:3 /W:1 /NP /SECFIX /XD \"temp\" \"D:\\src\\cache\" /XF \"*.tmp\" /XA:H",
            command.Arguments);
    }

    [Fact]
    public void Mirror_false_drops_mir_and_an_empty_copy_options_drops_the_defaults()
    {
        var command = Build(Config(mirror: false, copyOptions: ""));

        Assert.Equal("\"D:\\src\" \"E:\\dst\" /MT:16 /R:1 /W:1 /NP", command.Arguments);
        Assert.False(command.DeletesExtraFiles);
    }

    [Fact]
    public void A_trailing_backslash_does_not_swallow_the_closing_quote()
    {
        var args = Build(Config(source: @"D:\", destination: @"\\server\share\", mirror: false)).Arguments;

        Assert.StartsWith("\"D:\\\\\" \"\\\\server\\share\\\\\" ", args);
    }

    [Theory]
    [InlineData(@"E:\", null, null)]
    [InlineData(@"E:\", true, null)]
    [InlineData(@"\\server\share", true, null)]
    [InlineData(@"\\server\share\", null, null)]
    [InlineData(@"E:\", false, "/MIR")]
    [InlineData(@"E:\", false, "/PURGE")]
    [InlineData(@"E:\", false, "/move /B")]
    public void Refuses_destructive_operations_on_a_drive_or_share_root(string destination, bool? mirror, string? extra)
    {
        var ex = Assert.Throws<ActionConfigException>(() => Build(Config(destination: destination, mirror: mirror, extra: extra)));

        Assert.Contains("radice", ex.Message);
    }

    [Fact]
    public void Non_destructive_copies_to_a_root_and_a_mirror_to_a_folder_are_fine()
    {
        Build(Config(destination: @"E:\", mirror: false));
        Build(Config(destination: @"E:\dst"));
        Build(Config(destination: @"E:\", mirror: false, extra: "/MINAGE:3"));
        Build(Config(source: @"D:\src\a.dll", destination: @"E:\a.dll"), sourceIsFile: true);
    }

    [Fact]
    public void Rejects_invalid_values()
    {
        Assert.Throws<ActionConfigException>(() => Build(Config(source: null)));
        Assert.Throws<ActionConfigException>(() => Build(Config(source: "relative")));
        Assert.Throws<ActionConfigException>(() => Build(Config(source: @"D:\same", destination: @"d:\SAME\")));
        Assert.Throws<ActionConfigException>(() => Build(Config(threads: 0)));
        Assert.Throws<ActionConfigException>(() => Build(Config(threads: 129)));
        Assert.Throws<ActionConfigException>(() => Build(Config(retries: -1)));
        Assert.Throws<ActionConfigException>(() => Build(Config(files: ["a\"b"])));
    }

    [Theory]
    [InlineData(0, true, "nessuna differenza")]
    [InlineData(1, true, "file copiati")]
    [InlineData(3, true, "extra")]
    [InlineData(7, true, "non corrispondenti")]
    [InlineData(8, false, "non copiati")]
    [InlineData(9, false, "file copiati")]
    [InlineData(16, false, "errore grave")]
    public void Classifies_exit_codes_by_their_bits(int code, bool success, string text)
    {
        var result = RobocopyAction.Classify(code);

        Assert.Equal(success, result.Success);
        Assert.Contains(text, result.Message);
    }

    private Task<ActionResult> Run(FakeProcessRunner runner, object config, bool dryRun = false) =>
        new RobocopyAction(runner).ExecuteAsync(_env.Context(dryRun: dryRun), JsonSerializer.SerializeToElement(config));

    [Fact]
    public async Task Runs_robocopy_and_treats_exit_codes_below_8_as_success()
    {
        var runner = new FakeProcessRunner((_, _, _) => Task.FromResult(new ProcessResult(3, "Total Copied Skipped\r\nFiles : 4 4 0", "")));

        var result = await Run(runner, new { source = _env.Combine("s"), destination = _env.Combine("d") });

        var call = Assert.Single(runner.Calls);
        Assert.Equal("robocopy.exe", call.FileName);
        Assert.Contains("/MT:16", call.Arguments);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Exit_code_8_fails_the_step()
    {
        var runner = new FakeProcessRunner((_, _, _) => Task.FromResult(new ProcessResult(8, "", "")));

        var result = await Run(runner, new { source = _env.Combine("s"), destination = _env.Combine("d") });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Dry_run_runs_robocopy_in_list_only_mode()
    {
        var runner = new FakeProcessRunner((_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));

        await Run(runner, new { source = _env.Combine("s"), destination = _env.Combine("d"), mirror = true }, dryRun: true);

        Assert.EndsWith(" /L", Assert.Single(runner.Calls).Arguments);
    }

    [Fact]
    public async Task An_invalid_config_never_starts_a_process()
    {
        var runner = new FakeProcessRunner();

        await Assert.ThrowsAsync<ActionConfigException>(() => Run(runner, new { source = _env.Combine("s"), destination = @"E:\", mirror = true }));

        Assert.Empty(runner.Calls);
    }
}
