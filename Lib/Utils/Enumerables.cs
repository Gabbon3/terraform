using System.IO.Enumeration;
using lib.io;

namespace terraform.lib.utils;

/// <summary>
/// Wrapper di <see cref="FileSystemEnumerable{T}"/> per elencare file in streaming applicando un <see cref="FileSystemFilter"/>.
/// </summary>
/// <remarks>
/// Scelte di progetto:
/// - Il risultato è generico (T): la trasformazione legge direttamente il <see cref="FileSystemEntry"/>, quindi non si crea mai un FileSystemInfo
///   né una stringa che non serva al chiamante (per un conteggio basta restituire un int, per uno scan percorso e dimensione).
/// - Filtro e trasformazione ricevono lo stesso FileSystemEntry per ref: il filtro lavora su span, senza allocare.
/// - Il motore chiama la trasformazione solo per le entry che hanno superato il filtro: si paga solo per i file che corrispondono.
/// - La trasformazione è il delegate della BCL (<c>FindTransform</c>) e non uno nostro: nessun tipo da imparare e nessuna chiamata di inoltro per entry.
/// - FileSystemEntry è un ref struct e non può uscire dalla trasformazione: T deve copiare ciò che serve (percorso, dimensione, date).
/// - La visita esplicita per cartella permette di segnalare l'accesso negato e continuare con le cartelle sorelle.
/// - L'enumerazione è pigra e rilegge il disco a ogni giro; l'enumeratore non è thread-safe, quindi un consumatore alla volta
///   (Parallel.ForEachAsync serializza le MoveNext da sé).
/// </remarks>
public static class Enumerables
{
    /// <summary>
    /// File sotto <paramref name="directory"/> trasformati in <typeparamref name="T"/>; le cartelle non vengono mai restituite.
    /// </summary>
    /// <remarks>
    /// Include file nascosti e di sistema e non entra nelle junction, che possono formare cicli.
    /// Gli accessi negati sono notificati al callback, se presente; altrimenti l'eccezione viene propagata.
    /// La cartella radice deve esistere: verificarlo è compito del chiamante, altrimenti l'enumerazione lancia.
    /// </remarks>
    public static IEnumerable<T> Files<T>(
        string directory, FileSystemEnumerable<T>.FindTransform transform, FileSystemFilter? filter = null, bool recurse = true,
        Action<string, UnauthorizedAccessException>? onInaccessible = null, CancellationToken cancellationToken = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.None,
            ReturnSpecialDirectories = false
        };

        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerator<(string? Directory, T Value)> entries;
            try
            {
                entries = new FileSystemEnumerable<(string? Directory, T Value)>(current,
                    (ref FileSystemEntry entry) => entry.IsDirectory
                        ? (entry.ToFullPath(), default!)
                        : (null, transform(ref entry)), options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory
                        ? recurse && (entry.Attributes & FileAttributes.ReparsePoint) == 0
                        : filter is null || filter(ref entry)
                }.GetEnumerator();
            }
            catch (UnauthorizedAccessException ex) when (onInaccessible is not null)
            {
                onInaccessible(current, ex);
                continue;
            }

            using (entries)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // MoveNext puo' fallire anche dopo aver restituito alcune entry: conserviamo le sorelle gia' scoperte.
                    bool hasNext;
                    try
                    {
                        hasNext = entries.MoveNext();
                    }
                    catch (UnauthorizedAccessException ex) when (onInaccessible is not null)
                    {
                        onInaccessible(current, ex);
                        break;
                    }
                    if (!hasNext) break;
                    var entry = entries.Current;
                    if (entry.Directory is { } child)
                        pending.Push(child);
                    else
                        yield return entry.Value;
                }
            }
        }
    }

    /// <summary>Scorciatoia per i percorsi completi, il caso più comune.</summary>
    public static IEnumerable<string> Files(string directory, FileSystemFilter? filter = null, bool recurse = true,
        Action<string, UnauthorizedAccessException>? onInaccessible = null, CancellationToken cancellationToken = default) =>
        Files<string>(directory, static (ref FileSystemEntry entry) => entry.ToFullPath(), filter, recurse, onInaccessible, cancellationToken);
}
