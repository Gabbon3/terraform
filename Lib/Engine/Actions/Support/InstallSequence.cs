using System.Collections.Frozen;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.lib.engine.actions;

/// <summary>Un programma da lanciare in sequenza.</summary>
/// <param name="Name">Etichetta per i log.</param>
/// <param name="SourcePath">File che deve esistere (l'installer o il pacchetto).</param>
/// <param name="FileName">Programma da avviare: coincide con <paramref name="SourcePath"/> per un exe, è msiexec.exe per un msi.</param>
internal sealed record InstallItem(string Name, string SourcePath, string FileName, string Arguments, IReadOnlySet<int> SuccessExitCodes, bool IgnoreIfNotFound);

/// <summary>
/// Logica comune a <c>execute-process</c> e <c>msi-install</c>: esegue gli item uno dopo l'altro, in ordine.
/// </summary>
/// <remarks>
/// Si procede in sequenza e ci si ferma al primo fallimento, perché gli installer spesso dipendono l'uno dall'altro.
/// </remarks>
internal static class InstallSequence
{
    /// <summary>Codici di uscita di Windows Installer e degli installer: 3010 = riavvio richiesto, 1638 = versione già installata.</summary>
    private const int RebootRequired = 3010;
    private const int AlreadyInstalled = 1638;

    /// <summary>
    /// Usa i codici configurati, oppure <paramref name="fallback"/> se il campo è omesso.
    /// Un elenco vuoto è un errore: nessun codice conterebbe come successo, nemmeno 0.
    /// </summary>
    public static IReadOnlySet<int> ParseExitCodes(IReadOnlyList<int>? configured, IReadOnlySet<int> fallback, string field)
    {
        if (configured is null)
        {
            return fallback;
        }

        if (configured.Count == 0)
        {
            throw new ActionConfigException($"{field}: serve almeno un codice (omettere il campo per usare i default)");
        }

        return configured.ToFrozenSet();
    }

    public static async Task<ActionResult> RunAsync(ActionContext context, IProcessRunner runner, IReadOnlyList<InstallItem> items)
    {
        var dryRun = context.IsDryRun;

        // Nessun processo parte se manca un file obbligatorio. In dry-run no: uno step precedente (es. robocopy) potrebbe ancora doverlo copiare.
        var missing = items.Where(item => !item.IgnoreIfNotFound && !File.Exists(item.SourcePath)).Select(item => item.SourcePath).ToList();

        if (missing.Count > 0 && !dryRun)
        {
            return new ActionResult(false, $"file non trovati: {string.Join("; ", missing)}");
        }

        var done = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var exists = File.Exists(item.SourcePath);

            if (!exists && item.IgnoreIfNotFound)
            {
                context.Logger.Write($"{item.Name}: file non trovato, saltato ({item.SourcePath})", LogLevel.Warn);
                skipped++;
                continue;
            }

            if (dryRun)
            {
                var command = $"{item.FileName} {item.Arguments}".TrimEnd();
                context.Logger.Write($"[DRY-RUN] {command}{(exists ? "" : " (file non ancora presente)")}", exists ? LogLevel.Info : LogLevel.Warn);
                done++;
                continue;
            }

            context.Logger.Write($"{item.Name}: avvio...", LogLevel.Info);
            ProcessResult result;

            try
            {
                result = await runner.RunAsync(item.FileName, item.Arguments, context.CancellationToken);
            }
            // L'annullamento deve uscire e fermare lo step: non è un fallimento dell'item.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.Write($"{item.Name}: impossibile avviare il processo: {ex.Message}", LogLevel.Error);
                return new ActionResult(false, $"{item.Name}: {ex.Message}");
            }

            if (!item.SuccessExitCodes.Contains(result.ExitCode))
            {
                context.Logger.Write($"{item.Name}: fallito, exit code {result.ExitCode}", LogLevel.Error);
                return new ActionResult(false, $"{item.Name}: exit code {result.ExitCode}");
            }

            // Un successo con codice diverso da 0 va comunque notato: spesso significa che serve un riavvio.
            context.Logger.Write($"{item.Name}: completato{ExitNote(result.ExitCode)}", result.ExitCode == 0 ? LogLevel.Success : LogLevel.Warn);
            done++;
        }

        return new ActionResult(true, $"{done} {(dryRun ? "da eseguire" : "eseguiti")}, {skipped} saltati");
    }

    private static string ExitNote(int exitCode) => exitCode switch
    {
        0 => "",
        RebootRequired => " (riavvio richiesto)",
        AlreadyInstalled => " (già installato)",
        _ => $" (exit code {exitCode})"
    };
}
