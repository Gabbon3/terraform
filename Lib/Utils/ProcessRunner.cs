using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace terraform.lib.utils;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    /// <summary>Avvia <paramref name="fileName"/> senza shell e attende la fine; annullando il token uccide l'intero albero di processi.</summary>
    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    // Dell'output si conserva solo la coda: un processo prolisso non deve riempire la memoria.
    private const int MaxCapturedChars = 16 * 1024;

    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
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
            throw new InvalidOperationException($"impossibile avviare '{fileName}': {ex.Message}", ex);
        }

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
