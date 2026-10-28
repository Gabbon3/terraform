using System.Runtime.InteropServices;
using System.Text;
using terraform.lib.utils;

namespace terraform.Tests;

/// <summary>Verifica quoting e streaming senza dipendenze dalle Action.</summary>
public sealed class ProcessUtilsTests
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>Usa il parser Windows per verificare gli argomenti senza duplicare l'algoritmo sotto test.</summary>
    internal static string[] ParseArguments(string arguments)
    {
        var memory = CommandLineToArgvW("program.exe " + arguments, out var count);
        if (memory == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, index * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(memory);
        }
    }

    /// <summary>Recupera lo script per verificarlo o anteporre comandi stub nei test.</summary>
    internal static string Decode(string arguments) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Split(' ')[^1]));

    /// <summary>Preserva valori vuoti, spazi, virgolette e backslash adiacenti alle virgolette.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("secret'\"$; &")]
    [InlineData(@"C:\folder with spaces\")]
    [InlineData("a\\\\\"b\\")]
    [InlineData("\"C:\\Program Files\\batch.exe\" --name \"one two\"")]
    public void Arguments_roundtrip_through_the_windows_parser(string value)
    {
        Assert.Equal(new[] { value, "next" }, ParseArguments(ProcessUtils.QuoteArgument(value) + " next"));
    }

    /// <summary>La query usa streaming reale: una riga di oltre 16 KB deve arrivare intera.</summary>
    [Fact]
    public async Task Streams_complete_lines_and_ignores_blank_lines()
    {
        var lines = new List<string>();
        await foreach (var line in ProcessUtils.ExecuteAndStreamPowerShellAsync(
            new ProcessRunner(), "'first'; ''; ('x' * 40000); 'last'"))
            lines.Add(line);
        Assert.Equal(3, lines.Count);
        Assert.Equal("first", lines[0]);
        Assert.Equal(new string('x', 40000), lines[1]);
        Assert.Equal("last", lines[2]);
    }

    /// <summary>Un errore PowerShell, anche non terminante per default, deve fallire invece di sembrare zero risultati.</summary>
    [Fact]
    public async Task PowerShell_errors_are_explicit()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var line in ProcessUtils.ExecuteAndStreamPowerShellAsync(new ProcessRunner(), "Write-Error 'query failed'")) { }
        });
        Assert.Contains("query failed", error.Message);
        Assert.Contains("exit code 1", error.Message);
    }

    /// <summary>Un valore letterale non deve eseguire metacaratteri PowerShell.</summary>
    [Fact]
    public async Task Literal_values_are_not_interpreted_as_code()
    {
        const string value = "source'; throw 'injected'; $var";
        var lines = new List<string>();
        await foreach (var line in ProcessUtils.ExecuteAndStreamPowerShellAsync(new ProcessRunner(), ProcessUtils.PowerShellLiteral(value)))
            lines.Add(line);
        Assert.Equal(new[] { value }, lines);
        Assert.Equal("$null", ProcessUtils.PowerShellLiteral(null));
    }
}
