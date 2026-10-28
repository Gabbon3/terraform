using System.IO.Enumeration;
using lib.io;
using terraform.lib.utils;

namespace terraform.Tests;

public sealed class EnumerablesTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static FileSystemFilter GlobFilter(string pattern) =>
        FileFilterFactory.CreateFilter(new FileFilterFactory.FilterOptions(Pattern: pattern, MatchType: PatternMatchType.Glob))!;

    [Fact]
    public void The_transform_runs_only_for_entries_that_pass_the_filter()
    {
        _env.CreateFile(@"d\a.dll");
        _env.CreateFile(@"d\b.dll");
        _env.CreateFile(@"d\c.txt");
        var calls = 0;

        var names = Enumerables.Files<string>(_env.Combine("d"), (ref FileSystemEntry entry) =>
        {
            calls++;
            return entry.FileName.ToString();
        }, GlobFilter("*.dll")).Order().ToArray();

        Assert.Equal(new[] { "a.dll", "b.dll" }, names);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void The_result_can_be_any_type_read_straight_from_the_entry()
    {
        _env.CreateFile(@"d\five.txt", "12345");

        var (name, length) = Assert.Single(Enumerables.Files(_env.Combine("d"), (ref FileSystemEntry entry) => (Name: entry.FileName.ToString(), entry.Length)));

        Assert.Equal("five.txt", name);
        Assert.Equal(5, length);
    }

    [Fact]
    public void Returns_files_only_and_the_path_shortcut_gives_full_paths()
    {
        var top = _env.CreateFile(@"d\top.txt");
        var deep = _env.CreateFile(@"d\sub\deep.txt");

        var recursive = Enumerables.Files(_env.Combine("d")).Order().ToArray();
        var flat = Enumerables.Files(_env.Combine("d"), recurse: false).ToArray();

        Assert.Equal(new[] { deep, top }.Order(), recursive);
        Assert.Equal(new[] { top }, flat);
    }

    [Fact]
    public void Includes_hidden_files()
    {
        var hidden = _env.CreateFile(@"d\hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Equal(new[] { hidden }, Enumerables.Files(_env.Combine("d")).ToArray());
    }

    [Fact]
    public void A_missing_root_throws_so_callers_must_check_it_first()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Enumerables.Files(_env.Combine("absent")).ToArray());
    }
}
