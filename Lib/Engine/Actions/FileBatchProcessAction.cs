using System.Text.Json;
using lib.io;
using terraform.lib.console;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.lib.engine.actions;

/// <summary>
/// Azione <c>FILE-BATCH-PROCESS</c>: esegue un comando per ogni file trovato in <c>searchPaths</c>.
/// </summary>
/// <remarks>
/// Config: <c>searchPaths</c>, <c>recurse</c> (default false), <c>maxConcurrency</c> (default: numero di CPU), <c>filter</c>, <c>command</c>, <c>arguments</c>.
/// In <c>arguments</c> si possono usare <c>{FilePath}</c>, <c>{FileName}</c> e <c>{Directory}</c>, risolti per ogni file.
/// Tutti i file vengono elaborati anche se alcuni falliscono; lo step fallisce se almeno un comando esce con codice diverso da 0.
/// </remarks>
[ActionMetadata("Seleziona file e avvia un programma per ciascuno, con parallelismo limitato.")]
[ActionConfig(typeof(FileBatchProcessAction.Config))]
public sealed class FileBatchProcessAction(IProcessRunner processRunner) : IAction
{
    private const int MaxLoggedFailures = 20;
    private const int DryRunPreview = 20;

    private sealed class Config
    {
        [ConfigField("Cartelle complete da esaminare.", Required = true, Widget = "path")]
        public List<string>? SearchPaths { get; init; }
        [ConfigField("Include i file nelle sottocartelle.", DefaultJson = "false")]
        public bool Recurse { get; init; }
        [ConfigField("Numero massimo di processi paralleli; se omesso usa il numero di CPU.", Minimum = 1)]
        public int? MaxConcurrency { get; init; }
        [ConfigField("Criteri facoltativi per selezionare i file.")]
        public FileFilterConfig? Filter { get; init; }
        [ConfigField("Programma da avviare per ogni file.", Required = true)]
        public string? Command { get; init; }
        [ConfigField("Argomenti del programma; supporta i token per-file documentati nell'azione.", DefaultJson = "\"\"")]
        public string? Arguments { get; init; }
    }

    public string ActionType => "FILE-BATCH-PROCESS";
    public string ActionDescription => "Filtra file e avvia un comando per ciascuno, con parallelismo limitato.";

    /// <summary>Mostra configurazione del batch, filtri, token per file e comportamento sugli errori.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
                [Cyan]FILE-BATCH-PROCESS[/] - Esegue un comando per ogni file selezionato, con parallelismo limitato.
                [DarkGray]Esempio di step YAML:[/]
                id: batch # Obbligatorio e univoco.
                type: FILE-BATCH-PROCESS # Obbligatorio.
                name: Batch # Opzionale: etichetta per i log.
                enabled: true # Opzionale, default true.
                config:
                  searchPaths: ['D:\dll'] # Obbligatorio: almeno una cartella completa.
                  command: regsvr32.exe # Obbligatorio: programma, senza shell implicita.
                  arguments: '/s "{FilePath}"' # Opzionale, default vuoto.
                  recurse: false # Opzionale, default false.
                  maxConcurrency: 16 # Opzionale, default numero di CPU, minimo 1.
                  filter: # Opzionale: se omesso seleziona tutti i file.
                    pattern: '*.dll' # Opzionale: pattern sul nome del file.
                    matchType: Glob # Opzionale, default Auto: Auto, Fixed, Glob o Regex.
                    ignoreCase: true # Opzionale, default true.
                    matchFullPath: false # Opzionale, default false.
                    dateAfter: 30d # Opzionale: data minima, relativa o assoluta.
                    dateBefore: 1h # Opzionale: data massima; suffissi :m/:c/:a = modifica/creazione/accesso.
                    minSize: 1k # Opzionale: dimensione minima (byte, k, m, g, t).
                    maxSize: 5m # Opzionale: dimensione massima.
                """);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Configurazione e token");
        ConsolePlus.WriteList([
            "[Cyan]searchPaths[/] e [Cyan]command[/]: [Yellow]obbligatori[/]; percorsi completi e programma senza shell implicita.",
            "[Cyan]recurse[/]: default [Green]false[/]. [Cyan]maxConcurrency[/]: default [Green]numero di CPU[/], minimo 1.",
            "[Magenta]{FilePath}[/], [Magenta]{FileName}[/] (con estensione), [Magenta]{Directory}[/]: validi [Yellow]solo in arguments[/].",
            "[Magenta]{Nome}[/]: gli altri token provengono dalle [Cyan]variables[/] globali. [Yellow]Quotare i percorsi[/] negli argomenti."
        ]);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Filtri e comportamento");
        ConsolePlus.WriteList([
            "[Cyan]filter[/]: opzionale; omesso seleziona [Green]tutti i file[/].",
            "[Cyan]matchType: Auto[/]: usa [Green]Glob[/] con * o ?, altrimenti [Green]Fixed[/] (sottostringa); non riconosce le regex.",
            "[Cyan]Regex[/]: [Yellow]non supporta lookaround o backreference[/]. [Cyan]ignoreCase[/]: default true; [Cyan]matchFullPath[/]: default false.",
            "[Cyan]dateAfter / dateBefore[/]: date relative o assolute; suffissi [Magenta]:m / :c / :a[/] = modifica / creazione / accesso.",
            "[Cyan]minSize / maxSize[/]: byte o unita' [Magenta]k, m, g, t[/].",
            "[Green]Include file nascosti e di sistema[/]; [Yellow]non segue junction[/]. Root duplicate o annidate con recurse sono errori.",
            "[Yellow]Accesso negato[/] a file o cartelle: Warning con percorso e [Green]prosecuzione[/] sugli altri elementi.",
            "[Yellow]Zero file[/]: Warning e successo. [Yellow]Exit code diverso da 0[/]: il batch continua, poi fallisce lo step.",
            "[Cyan]Dry-run[/]: [Green]nessun processo[/]; mostra i primi [Cyan]20 comandi[/] e il totale. Cartelle mancanti: solo Warning."
        ]);
    }

    public IReadOnlySet<string> ItemTokens => VariableEngine.ReservedNames;

    /// <summary>Valida campi e filtro senza enumerare file o avviare comandi.</summary>
    public void Validate(JsonElement config) => ReadConfig(config);

    /// <summary>Valida tutti i campi, compreso il filtro, senza dipendere dallo stato del filesystem.</summary>
    private static (Config Settings, List<string> Roots, FileSystemFilter? Filter) ReadConfig(JsonElement config)
    {
        var settings = ActionConfig.Read<Config>(config);
        var command = ActionConfig.Required(settings.Command, "command");
        var maxConcurrency = settings.MaxConcurrency ?? Environment.ProcessorCount;

        if (maxConcurrency < 1)
        {
            throw new ActionConfigException($"maxConcurrency: deve essere almeno 1, trovato {maxConcurrency}");
        }

        // Il pre-flight lascia intatti i token per-file ovunque: fuori da "arguments" non avrebbero alcun valore.
        RejectItemTokens(command, "command");
        RejectItemTokens(settings.Filter?.Pattern, "filter.pattern");
        var roots = ValidateRoots(settings, out var rootErrors);

        if (rootErrors.Count > 0)
        {
            throw new ActionConfigException(string.Join("; ", rootErrors));
        }

        return (settings, roots, settings.Filter?.Build());
    }

    /// <summary>Elenca i file accessibili e lancia i comandi con parallelismo limitato; gli errori nativi restano bloccanti a fine batch.</summary>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var (settings, roots, filter) = ReadConfig(config);
        var command = settings.Command!;
        var arguments = settings.Arguments ?? "";
        var maxConcurrency = settings.MaxConcurrency ?? Environment.ProcessorCount;
        foreach (var root in roots.ToArray())
        {
            // Directory.Exists nasconde l'accesso negato: GetAttributes permette invece di distinguerlo.
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
                    throw new ActionConfigException($"searchPaths: non e' una cartella: {root}");
            }
            catch (UnauthorizedAccessException ex)
            {
                context.Logger.Write($"{root}: accesso negato, saltato: {ex.Message}", LogLevel.Warn);
                roots.Remove(root);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!context.IsDryRun)
                    throw new ActionConfigException($"searchPaths: cartella inesistente: {root}");
                context.Logger.Write($"[DRY-RUN] cartella inesistente, nessun file elencabile: {root}", LogLevel.Warn);
                roots.Remove(root);
            }
        }

        var token = context.CancellationToken;

        // Interlocked a ogni file: costa nanosecondi contro i millisecondi dell'avvio di un processo, e ForEachAsync non offre stato per worker.
        long processed = 0;
        long failed = 0;

        if (context.IsDryRun)
        {
            return DryRun(context, roots, settings.Recurse, filter, command, arguments);
        }

        var files = EnumerateFiles(context, roots, settings.Recurse, filter);
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = token };

        // Su un IEnumerable ForEachAsync serializza le MoveNext: il disco si legge man mano che i worker si liberano, senza mai caricare l'elenco intero.
        await Parallel.ForEachAsync(files, options, async (path, itemToken) =>
        {
            Interlocked.Increment(ref processed);

            try
            {
                var result = await processRunner.RunAsync(command, ResolveArguments(context, arguments, path), itemToken);

                if (result.ExitCode == 0)
                {
                    return;
                }

                RecordFailure($"{path}: exit code {result.ExitCode}{Describe(result)}");
            }
            // L'annullamento non è un fallimento del file: deve uscire e fermare il batch.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure($"{path}: {ex.Message}");
            }
        });

        if (processed == 0)
        {
            context.Logger.Write("nessun file corrisponde ai criteri indicati", LogLevel.Warn);
            return new ActionResult(true, "nessun file corrispondente");
        }

        if (failed > 0)
        {
            var hidden = failed > MaxLoggedFailures ? $" (mostrati i primi {MaxLoggedFailures})" : "";
            return new ActionResult(false, $"{failed} falliti su {processed} file{hidden}");
        }

        return new ActionResult(true, $"{processed} file elaborati");

        // Ogni fallimento viene contato, ma solo i primi finiscono nel log: con migliaia di file lo inonderebbero.
        void RecordFailure(string message)
        {
            if (Interlocked.Increment(ref failed) <= MaxLoggedFailures)
            {
                context.Logger.Write(message, LogLevel.Error, isParallel: true);
            }
        }
    }

    /// <summary>
    /// Mostra i comandi che verrebbero lanciati (i primi <see cref="DryRunPreview"/>, poi solo il totale) senza avviare processi.
    /// </summary>
    private static ActionResult DryRun(ActionContext context, List<string> roots, bool recurse, FileSystemFilter? filter, string command, string arguments)
    {
        long total = 0;

        foreach (var path in EnumerateFiles(context, roots, recurse, filter))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (total++ < DryRunPreview)
            {
                context.Logger.Write($"[DRY-RUN] {command} {ResolveArguments(context, arguments, path)}", LogLevel.Info);
            }
        }

        if (total == 0)
        {
            context.Logger.Write("nessun file corrisponde ai criteri indicati", LogLevel.Warn);
            return new ActionResult(true, "nessun file corrispondente");
        }

        if (total > DryRunPreview)
        {
            context.Logger.Write($"[DRY-RUN] ... e altri {total - DryRunPreview} comandi", LogLevel.Info);
        }

        return new ActionResult(true, $"{total} comandi da eseguire");
    }

    /// <summary>
    /// Sostituisce <c>{FilePath}</c>, <c>{FileName}</c> (con estensione) e <c>{Directory}</c> nel template degli argomenti.
    /// </summary>
    /// <remarks>
    /// Gli altri token li ha già risolti il runner prima dell'esecuzione. Per un file in una radice di unità <c>Directory</c> termina con
    /// backslash (<c>C:\</c>): tra virgolette finirebbe per "escapare" la virgoletta di chiusura.
    /// </remarks>
    private static string ResolveArguments(ActionContext context, string template, string path)
    {
        var scope = new Dictionary<string, string>(3)
        {
            ["FilePath"] = path,
            ["FileName"] = Path.GetFileName(path),
            ["Directory"] = Path.GetDirectoryName(path) ?? ""
        };

        return context.Variables.Resolve(template, scope);
    }

    /// <summary>
    /// Motivo breve del fallimento: la prima riga di stderr (o di stdout se stderr è vuoto), tagliata a 200 caratteri,
    /// abbastanza per capire cosa è andato storto senza riversare l'output intero nel log.
    /// </summary>
    private static string Describe(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        text = text.Trim();

        if (text.Length == 0)
        {
            return "";
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        return $" - {(firstLine.Length > 200 ? firstLine[..200] : firstLine)}";
    }

    /// <summary>
    /// Rifiuta i token per-file nei campi dove non hanno un valore (<c>command</c>, <c>searchPaths</c>, <c>filter.pattern</c>).
    /// </summary>
    /// <remarks>
    /// Il runner lascia intatti <c>{FilePath}</c>, <c>{FileName}</c> e <c>{Directory}</c> in tutta la config di questa azione, perché è lei a risolverli:
    /// fuori da <c>arguments</c> resterebbero testo letterale e creerebbero percorsi o comandi con le graffe dentro.
    /// </remarks>
    private static void RejectItemTokens(string? value, string field)
    {
        if (value is not null && VariableEngine.ContainsToken(value, VariableEngine.ReservedNames))
        {
            throw new ActionConfigException($"{field}: i token {{FilePath}}, {{FileName}} e {{Directory}} sono ammessi solo in 'arguments'");
        }
    }

    /// <summary>
    /// Normalizza e controlla <c>searchPaths</c>, raccogliendo tutti gli errori invece di fermarsi al primo.
    /// </summary>
    /// <remarks>
    /// Per ogni percorso:
    /// 1. rifiuta i token per-file e i percorsi non completi;
    /// 2. la confronta con le root già accettate: se coincidono, o se una contiene l'altra con <c>recurse</c> attivo,
    ///    gli stessi file verrebbero visitati due volte e il comando girerebbe due volte sullo stesso file.
    /// Senza <c>recurse</c> due root annidate non si sovrappongono (ognuna guarda solo i file della propria cartella), quindi sono ammesse.
    /// </remarks>
    /// <param name="errors">Messaggi di errore; vuoto se la config è valida.</param>
    /// <returns>Le root normalizzate, senza backslash finale tranne le radici di unità come <c>C:\</c>.</returns>
    private static List<string> ValidateRoots(Config settings, out List<string> errors)
    {
        errors = [];
        var roots = new List<string>();

        if (settings.SearchPaths is not { Count: > 0 })
        {
            errors.Add("searchPaths: serve almeno un percorso");
            return roots;
        }

        for (var i = 0; i < settings.SearchPaths.Count; i++)
        {
            RejectItemTokens(settings.SearchPaths[i], $"searchPaths[{i}]");
            var root = Path.TrimEndingDirectorySeparator(ActionConfig.FullPath(settings.SearchPaths[i], $"searchPaths[{i}]"));

            foreach (var other in roots)
            {
                if (string.Equals(root, other, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"searchPaths[{i}]: percorso duplicato: {root}");
                }
                else if (settings.Recurse && (IsInside(root, other) || IsInside(other, root)))
                {
                    errors.Add($"searchPaths[{i}]: {root} e {other} si sovrappongono con recurse attivo");
                }
            }

            roots.Add(root);
        }

        return roots;
    }

    /// <summary>
    /// True se <paramref name="path"/> sta dentro <paramref name="parent"/>. Si confronta il prefisso con il separatore finale,
    /// così <c>C:\data2</c> non risulta contenuto in <c>C:\data</c>.
    /// </summary>
    private static bool IsInside(string path, string parent)
    {
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Elenca i file e segnala gli accessi negati senza interrompere il batch. Le root mancanti in dry-run sono gia' segnalate.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(ActionContext context, IReadOnlyList<string> roots, bool recurse, FileSystemFilter? filter)
    {
        foreach (var root in roots)
        {
            foreach (var path in Enumerables.Files(root, filter, recurse,
                (denied, error) => context.Logger.Write($"{denied}: accesso negato, saltato: {error.Message}", LogLevel.Warn, isParallel: true),
                context.CancellationToken))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                // L'elenco puo' essere leggibile anche se il contenuto del singolo file non lo e'.
                try
                {
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (UnauthorizedAccessException ex)
                {
                    context.Logger.Write($"{path}: accesso negato, saltato: {ex.Message}", LogLevel.Warn, isParallel: true);
                    continue;
                }
                yield return path;
            }
        }
    }
}
