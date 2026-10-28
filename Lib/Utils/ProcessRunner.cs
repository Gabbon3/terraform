using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace terraform.lib.utils;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    /// <summary>Avvia <paramref name="fileName"/> senza shell e attende la fine; annullando il token uccide l'intero albero di processi.</summary>
    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);

    /// <summary>Emette stdout riga per riga; fallisce su exit code non zero e chiude il processo se la lettura si interrompe.</summary>
    IAsyncEnumerable<string> StreamAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    // Dell'output si conserva solo la coda: un processo prolisso non deve riempire la memoria.
    private const int MaxCapturedChars = 16 * 1024;

    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Start(fileName, arguments);

        var output = new TailBuffer(MaxCapturedChars);
        var error = new TailBuffer(MaxCapturedChars);
        var drains = Task.WhenAll(Drain(process.StandardOutput, output), Drain(process.StandardError, error));

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await drains.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>Legge righe complete senza il limite di cattura, drenando stderr in parallelo.</summary>
    /// <param name="fileName">Eseguibile da avviare senza shell.</param>
    /// <param name="arguments">Argomenti gia' quotati per l'eseguibile.</param>
    /// <param name="cancellationToken">Token che interrompe lettura e processo.</param>
    /// <returns>Righe di stdout nell'ordine originale.</returns>
    /// <remarks>Il finally copre anche break, errori del consumer e annullamento tra due righe.</remarks>
    public async IAsyncEnumerable<string> StreamAsync(
        string fileName, string arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Start(fileName, arguments);
        var error = new TailBuffer(MaxCapturedChars);
        var drain = Drain(process.StandardError, error);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
            }
            await process.WaitForExitAsync(cancellationToken);
            await drain.WaitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{fileName}: exit code {process.ExitCode} - {error}");
            }
        }
        finally
        {
            Kill(process);
            await drain;
        }
    }

    /// <summary>Avvia un processo con entrambi i canali rediretti, condividendo le stesse opzioni tra i due flow.</summary>
    private static Process Start(string fileName, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"impossibile avviare '{fileName}': {ex.Message}", ex);
        }
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task Drain(StreamReader reader, TailBuffer buffer)
    {
        var chunk = new char[4096];

        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk)) > 0)
            {
                buffer.Append(chunk, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private sealed class TailBuffer(int capacity)
    {
        private readonly StringBuilder _text = new();
        private readonly object _lock = new();

        public void Append(char[] chunk, int length)
        {
            lock (_lock)
            {
                _text.Append(chunk, 0, length);

                if (_text.Length > capacity)
                {
                    _text.Remove(0, _text.Length - capacity);
                }
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _text.ToString();
            }
        }
    }
}
