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
/// - L'enumerazione è pigra e rilegge il disco a ogni giro; l'enumeratore non è thread-safe, quindi un consumatore alla volta
///   (Parallel.ForEachAsync serializza le MoveNext da sé).
/// </remarks>
public static class Enumerables
{
    /// <summary>
    /// File sotto <paramref name="directory"/> trasformati in <typeparamref name="T"/>; le cartelle non vengono mai restituite.
    /// </summary>
    /// <remarks>
    /// Include file nascosti e di sistema, ignora le cartelle inaccessibili e non entra nelle junction, che possono formare cicli.
    /// La cartella radice deve esistere: verificarlo è compito del chiamante, altrimenti l'enumerazione lancia.
    /// </remarks>
    public static FileSystemEnumerable<T> Files<T>(
        string directory, FileSystemEnumerable<T>.FindTransform transform, FileSystemFilter? filter = null, bool recurse = true)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            ReturnSpecialDirectories = false
        };

        // Il predicate si sceglie una volta sola, invece di controllare filter su ogni entry.
        FileSystemEnumerable<T>.FindPredicate include = filter is null
            ? static (ref FileSystemEntry entry) => !entry.IsDirectory
            : (ref FileSystemEntry entry) => !entry.IsDirectory && filter(ref entry);

        return new FileSystemEnumerable<T>(directory, transform, options)
        {
            ShouldIncludePredicate = include,
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
        };
    }

    /// <summary>Scorciatoia per i percorsi completi, il caso più comune.</summary>
    public static FileSystemEnumerable<string> Files(string directory, FileSystemFilter? filter = null, bool recurse = true) =>
        Files<string>(directory, static (ref FileSystemEntry entry) => entry.ToFullPath(), filter, recurse);
}
