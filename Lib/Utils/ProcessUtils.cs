using System.Diagnostics;
using System.Text;
using System.Runtime.CompilerServices;

namespace terraform.lib.utils;

public static class ProcessUtils
{
    /// <summary>Esegue uno script e restituisce lo stdout riga per riga, senza bufferizzare l'intero export.</summary>
    /// <param name="runner">Runner sostituibile nei test; gestisce processo, stderr ed exit code.</param>
    /// <param name="script">Script che produce una riga NDJSON per elemento; non deve contenere password.</param>
    /// <param name="cancellationToken">Annulla lettura e processo anche durante l'elaborazione degli elementi.</param>
    /// <returns>Righe non vuote emesse da PowerShell, senza il limite di cattura di RunAsync.</returns>
    /// <remarks>Overload per la pipeline; il metodo storico senza runner resta invariato per il migrator.</remarks>
    public static async IAsyncEnumerable<string> ExecuteAndStreamPowerShellAsync(
        IProcessRunner runner, string script, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Stop + catch esplicito: una query remota fallita non deve sembrare una selezione vuota riuscita.
        var command = "$ErrorActionPreference = 'Stop'\ntry {\n" + script + """

            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        var arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        if (arguments.Length > 32000)
        {
            throw new InvalidOperationException("Script PowerShell oltre il limite della command line Windows.");
        }
        await foreach (var line in runner.StreamAsync("powershell.exe", arguments, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    /// <summary>Converte un valore configurato in una stringa letterale PowerShell; null diventa $null.</summary>
    /// <remarks>Gli apostrofi raddoppiati impediscono di interpretare i dati come codice.</remarks>
    public static string PowerShellLiteral(string? value) => value is null ? "$null" : "'" + value.Replace("'", "''") + "'";

    /// <summary>Quota un singolo argomento per un eseguibile Windows avviato senza shell.</summary>
    /// <param name="value">Valore originale, inclusi eventuali spazi, virgolette e backslash finali.</param>
    /// <returns>Argomento quotato secondo le regole Windows; non e' escaping per cmd.exe o PowerShell.</returns>
    public static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            // Prima di una virgoletta, raddoppiare i backslash e aggiungerne uno per la virgoletta stessa.
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static async Task<string> ExecutePowerShellAsync(string script)
    {
        // 1. Codifica in Base64 (UTF-16LE, ovvero Unicode) per evitare qualsiasi problema di sintassi
        var bytes = System.Text.Encoding.Unicode.GetBytes(script);
        var encodedCommand = Convert.ToBase64String(bytes);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Fondamentale leggere anche questo
            CreateNoWindow = true
        };

        process.Start();

        // 2. Lettura asincrona in parallelo di entrambi i canali per evitare Deadlock di sistema
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

        // Opzionale: log dell'errore se serve in futuro (errorTask.Result)

        return outputTask.Result;
    }

    public static async IAsyncEnumerable<string> ExecuteAndStreamPowerShellAsync(string script)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(script);
        var encodedCommand = Convert.ToBase64String(bytes);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();

        // Svuotiamo il buffer degli errori in background per evitare il deadlock
        var errorDrainTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                await process.StandardError.ReadLineAsync();
            }
        });

        // Leggiamo ed emettiamo l'output riga per riga (NDJSON)
        while (!process.StandardOutput.EndOfStream)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }

        await Task.WhenAll(errorDrainTask, process.WaitForExitAsync());
    }

    public static async Task<int> RunProcessAsync(string fileName, string arguments, bool suppressOutput = false)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = suppressOutput,
            RedirectStandardError = suppressOutput
        });

        if (process == null)
        {
            return -1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
