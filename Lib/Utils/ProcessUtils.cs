using System.Diagnostics;

namespace terraform.lib.utils;

public static class ProcessUtils
{
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
