using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using terraform.lib.utils;

namespace terraform.Tests;

// Runner finto: registra le chiamate e misura la concorrenza massima osservata.
internal sealed class FakeProcessRunner(
    Func<string, string, CancellationToken, Task<ProcessResult>>? handler = null,
    Func<string, string, CancellationToken, IAsyncEnumerable<string>>? streamHandler = null) : IProcessRunner
{
    private int _current;
    private int _max;

    public ConcurrentQueue<(string FileName, string Arguments)> Calls { get; } = new();

    public int MaxObserved => Volatile.Read(ref _max);

    public async IAsyncEnumerable<string> StreamAsync(
        string fileName, string arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Enqueue((fileName, arguments));
        if (streamHandler is null)
            throw new InvalidOperationException("Stream handler non configurato nel test.");
        await foreach (var line in streamHandler(fileName, arguments, cancellationToken))
            yield return line;
    }

    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        Calls.Enqueue((fileName, arguments));
        var now = Interlocked.Increment(ref _current);

        int seen;
        while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
        {
        }

        try
        {
            if (handler is not null)
            {
                return await handler(fileName, arguments, cancellationToken);
            }

            await Task.Delay(30, cancellationToken);
            return new ProcessResult(0, "", "");
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}
