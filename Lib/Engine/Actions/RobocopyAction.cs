using System.Text.Json;
using System.Text.RegularExpressions;
using terraform.lib.console;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.lib.engine.actions;

public sealed class RobocopyConfig
{
    public const string DefaultCopyOptions = "/COPYALL /B /DCOPY:DAT";

    // Una cartella o un singolo file; per un file la destinazione è il percorso completo del file copiato.
    public string? Source { get; init; }
    public string? Destination { get; init; }

    // Solo per sorgenti cartella.
    public List<string>? Files { get; init; }
    public List<string>? ExcludeDirs { get; init; }
    public List<string>? ExcludeFiles { get; init; }

    // Default true per le cartelle (/MIR, come il migrator): elimina dalla destinazione ciò che non è nella sorgente.
    public bool? Mirror { get; init; }

    // Default 16, solo per sorgenti cartella.
    public int? Threads { get; init; }
    public int? Retries { get; init; }
    public int? WaitSeconds { get; init; }

    // Sostituisce /COPYALL /B /DCOPY:DAT; stringa vuota per nessuna opzione di copia.
    public string? CopyOptions { get; init; }

    // Aggiunto in coda, senza toccare i default.
    public string? ExtraArguments { get; init; }
}

public sealed record RobocopyCommand(string Arguments, bool DeletesExtraFiles);

/// <summary>
/// Azione <c>ROBOCOPY</c>. Con la config minima (source e destination) usa gli stessi flag del migrator.
/// </summary>
/// <remarks>
/// Flag di default per una sorgente cartella:
/// - <c>/MIR</c>: mirror, elimina dalla destinazione ciò che non è nella sorgente (disattivabile con <c>mirror: false</c>).
/// - <c>/MT:16</c>: copia multithread con 16 thread (<c>threads</c>).
/// - <c>/COPYALL</c>: copia dati, attributi, timestamp, ACL, owner e audit (<c>copyOptions</c>).
/// - <c>/B</c>: backup mode, legge e scrive anche file con permessi restrittivi se il processo ha i privilegi (<c>copyOptions</c>).
/// - <c>/DCOPY:DAT</c>: copia data, attributi e timestamp delle directory (<c>copyOptions</c>).
/// - <c>/R:1</c> e <c>/W:1</c>: un solo retry con un secondo di attesa (<c>retries</c>, <c>waitSeconds</c>).
/// - <c>/NP</c>: niente percentuale di avanzamento, per log più compatti.
/// - <c>/SECFIX</c>: riapplica la sicurezza anche ai file già presenti; aggiunto solo se la destinazione esiste già.
///
/// Per una sorgente file valgono gli stessi flag tranne <c>/MIR</c> e <c>/MT</c>.
/// In dry-run si aggiunge <c>/L</c> (solo elenco, nessuna copia né cancellazione).
/// <c>/XD</c> e <c>/XF</c> compaiono solo se configurati; <c>extraArguments</c> viene accodato in fondo.
/// </remarks>

public sealed partial class RobocopyAction(IProcessRunner processRunner) : IAction
{
    private const int SummaryLines = 12;

    [GeneratedRegex("""(?<!\S)"?/(MIR|PURGE|MOVE?)(?![A-Za-z0-9])""", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveSwitch();

    [GeneratedRegex("""(?<!\S)"?/MOVE?(?![A-Za-z0-9])""", RegexOptions.IgnoreCase)]
    private static partial Regex MoveSwitch();

    public string ActionType => "ROBOCOPY";
    public string ActionDescription => "Copia file e cartelle con Robocopy, preservando metadati e applicando le opzioni configurate.";

    /// <summary>Mostra configurazione Robocopy, default di copia e avvertenze sulle operazioni distruttive.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
                [Cyan]ROBOCOPY[/] - Copia cartelle o un file con i metadati e le opzioni configurate.
                                [DarkGray]Esempio di step YAML:[/]
                                id: copy # Obbligatorio e univoco.
                                type: ROBOCOPY # Obbligatorio.
                                name: Copia # Opzionale: etichetta per i log.
                                enabled: true # Opzionale, default true.
                                config:
                                    source: '\\{SourceServer}\d$\data' # Obbligatorio: percorso completo della sorgente.
                                    destination: 'D:\data' # Obbligatorio: percorso completo; per file, include il nome file.
                                    mirror: true # Opzionale, default true per cartelle: ELIMINA gli elementi extra in destinazione.
                                    threads: 16 # Opzionale, default 16, da 1 a 128; solo cartelle.
                                    retries: 1 # Opzionale, default 1, minimo 0. Se la copia fallisce, quanti tentativi ripetere.
                                    waitSeconds: 1 # Opzionale, default 1, minimo 0.
                                    copyOptions: '/COPYALL /B /DCOPY:DAT' # Opzionale: questi sono i default, vuoto li rimuove.
                                    files: ['*.dll'] # Opzionale: filtri file, solo per sorgente cartella.
                                    excludeDirs: [logs] # Opzionale: esclusioni /XD, solo cartelle.
                                    excludeFiles: ['*.tmp'] # Opzionale: esclusioni /XF, solo cartelle.
                                    extraArguments: '' # Opzionale: opzioni aggiuntive accodate.
                """);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Configurazione e default");
        ConsolePlus.WriteList([
            "[Cyan]source / destination[/]: [Yellow]obbligatori[/], percorsi completi. Per un file, destination include il nome del file.",
            "[Magenta]{SourceServer}[/]: va definita nelle [Cyan]variables[/]. Omettere [Cyan]files[/] per copiare tutti i file.",
            "[Yellow]mirror: true (default per cartelle)[/]: usa [Yellow]/MIR[/] ed [Yellow]ELIMINA gli elementi extra in destinazione[/]. [Cyan]mirror: false[/] disattiva /MIR.",
            "[Cyan]threads[/]: default [Green]16[/], da 1 a 128; [Cyan]retries / waitSeconds[/]: default [Green]1[/], minimo 0.",
            "[Cyan]copyOptions[/]: default [Green]/COPYALL /B /DCOPY:DAT[/]; stringa vuota rimuove queste opzioni.",
            "[Cyan]/COPYALL[/]: dati, attributi, date e sicurezza. [Cyan]/B[/]: privilegi backup. [Cyan]/DCOPY:DAT[/]: metadati directory.",
            "[Cyan]/NP[/]: sempre presente. [Cyan]/SECFIX[/]: riapplica la sicurezza se la cartella destinazione esiste gia'.",
            "[Cyan]extraArguments[/]: opzioni aggiuntive accodate, senza sostituire i default."
        ]);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Vincoli e simulazione");
        ConsolePlus.WriteList([
            "[Yellow]Sorgente file[/]: niente [Cyan]/MIR[/] o [Cyan]/MT[/]; [Cyan]files / excludeDirs / excludeFiles[/] non ammessi.",
            "[Yellow]Sorgente e destinazione sovrapposte vietate[/], in entrambe le direzioni.",
            "[Yellow]Operazioni distruttive vietate[/] verso radici di unita' o share; [Cyan]/MOV e /MOVE[/] vietati anche da una radice sorgente.",
            "[Cyan]Exit code[/]: [Green]0-7 = successo[/]; [Yellow]da 8 = errore[/].",
            "[Cyan]Dry-run[/]: avvia Robocopy con [Magenta]/L[/], [Green]solo elenco, nessuna copia o cancellazione[/]."
        ]);
    }

    /// <summary>Valida opzioni e percorsi senza interrogare disco o share; il tipo file/cartella viene verificato a runtime.</summary>
    public void Validate(JsonElement config) =>
        Build(ActionConfig.Read<RobocopyConfig>(config), false, _ => false, _ => false);

    public async Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var settings = ActionConfig.Read<RobocopyConfig>(config);
        var command = Build(settings, context.IsDryRun);
        var arguments = command.Arguments;

        if (command.DeletesExtraFiles)
        {
            var scope = context.IsDryRun ? " (simulazione con /L)" : "";
            context.Logger.Write($"mirror attivo: i file extra in {settings.Destination} verranno eliminati{scope}", LogLevel.Warn);
        }

        context.Logger.Write($"robocopy {arguments}", LogLevel.Info);
        var result = await processRunner.RunAsync("robocopy.exe", arguments, context.CancellationToken);
        var outcome = Classify(result.ExitCode);

        foreach (var line in Tail(result.StandardOutput))
        {
            context.Logger.Write(line, outcome.Success ? LogLevel.Info : LogLevel.Error);
        }

        return outcome;
    }

    /// <summary>
    /// Costruisce e valida la riga di comando; in dry-run aggiunge /L, che non modifica nulla.
    /// I default replicano il migrator; i delegati sostituiscono l'accesso al disco nei test.
    /// </summary>
    public static RobocopyCommand Build(RobocopyConfig config, bool dryRun, Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;

        var source = ActionConfig.FullPath(config.Source, "source");
        var destination = ActionConfig.FullPath(config.Destination, "destination");
        // Controllo lessicale prima di ogni I/O: non permette copie dentro se stesse o mirror su un proprio antenato.
        // I separatori finali evitano falsi positivi come D:\data e D:\database.
        if (IsInside(source, destination) || IsInside(destination, source))
        {
            throw new ActionConfigException("source e destination si sovrappongono: una contiene l'altra");
        }
        var sourceIsFile = fileExists(source);
        var retries = config.Retries ?? 1;
        var wait = config.WaitSeconds ?? 1;
        var copyOptions = (config.CopyOptions ?? RobocopyConfig.DefaultCopyOptions).Trim();
        var extra = config.ExtraArguments?.Trim() ?? "";

        // Per un file robocopy lavora sulle cartelle e il nome del file è un filtro.
        var sourceDir = sourceIsFile ? Path.GetDirectoryName(source)! : source;
        var destinationDir = sourceIsFile
            ? Path.GetDirectoryName(destination) ?? throw new ActionConfigException($"destination: con una sorgente file serve il percorso completo del file, trovato '{destination}'")
            : destination;
        var mirror = !sourceIsFile && (config.Mirror ?? true);

        if (sourceIsFile && (config.Files is { Count: > 0 } || config.ExcludeDirs is { Count: > 0 } || config.ExcludeFiles is { Count: > 0 }))
        {
            throw new ActionConfigException("files, excludeDirs ed excludeFiles non sono ammessi con una sorgente file");
        }

        if (string.Equals(Path.TrimEndingDirectorySeparator(sourceDir), Path.TrimEndingDirectorySeparator(destinationDir), StringComparison.OrdinalIgnoreCase))
        {
            throw new ActionConfigException("source e destination coincidono");
        }

        if (config.Threads is < 1 or > 128)
        {
            throw new ActionConfigException($"threads: deve essere tra 1 e 128, trovato {config.Threads}");
        }

        if (retries < 0 || wait < 0)
        {
            throw new ActionConfigException("retries e waitSeconds non possono essere negativi");
        }

        // /MIR, /PURGE e /MOVE cancellano: su una radice di unità un errore di configurazione sarebbe irreparabile.
        var deletes = mirror || DestructiveSwitch().IsMatch($"{copyOptions} {extra}");

        if (MoveSwitch().IsMatch($"{copyOptions} {extra}") && IsRoot(sourceDir))
        {
            throw new ActionConfigException($"source: /MOV e /MOVE non sono ammessi dalla radice '{sourceDir}'");
        }

        if (deletes && IsRoot(destinationDir))
        {
            throw new ActionConfigException($"destination: un'operazione che cancella file non è ammessa sulla radice '{destinationDir}' (per copiare senza cancellare usare mirror: false)");
        }

        var args = new List<string> { Quote(sourceDir, "source"), Quote(destinationDir, "destination") };

        if (sourceIsFile)
        {
            args.Add(Quote(Path.GetFileName(source), "source"));
        }

        args.AddRange(Items(config.Files, "files"));

        if (mirror)
        {
            args.Add("/MIR");
        }

        if (!sourceIsFile)
        {
            args.Add($"/MT:{config.Threads ?? 16}");
        }

        if (copyOptions.Length > 0)
        {
            args.Add(copyOptions);
        }

        args.AddRange([$"/R:{retries}", $"/W:{wait}", "/NP"]);

        if (directoryExists(destinationDir))
        {
            args.Add("/SECFIX");
        }

        if (config.ExcludeDirs is { Count: > 0 })
        {
            args.Add("/XD");
            args.AddRange(Items(config.ExcludeDirs, "excludeDirs"));
        }

        if (config.ExcludeFiles is { Count: > 0 })
        {
            args.Add("/XF");
            args.AddRange(Items(config.ExcludeFiles, "excludeFiles"));
        }

        if (extra.Length > 0)
        {
            args.Add(extra);
        }

        if (dryRun)
        {
            args.Add("/L");
        }

        return new RobocopyCommand(string.Join(' ', args), deletes);
    }

    /// <summary>Robocopy usa bit di stato: da 8 in su è un fallimento, sotto è successo anche se ha copiato o rimosso file.</summary>
    public static ActionResult Classify(int exitCode)
    {
        var parts = new List<string>();

        if ((exitCode & 1) != 0) parts.Add("file copiati");
        if ((exitCode & 2) != 0) parts.Add("file o cartelle extra nella destinazione");
        if ((exitCode & 4) != 0) parts.Add("file o cartelle non corrispondenti");
        if ((exitCode & 8) != 0) parts.Add("alcuni file non copiati");
        if ((exitCode & 16) != 0) parts.Add("errore grave, nessuna copia eseguita");

        var detail = parts.Count == 0 ? "nessuna differenza" : string.Join(", ", parts);
        return new ActionResult(exitCode < 8, $"robocopy exit {exitCode}: {detail}");
    }

    // GetPathRoot omette il backslash finale per le share UNC (\\server\share), quindi si confronta senza.
    private static bool IsRoot(string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string parent) =>
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + "\\", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Items(List<string>? values, string field) =>
        (values ?? []).Select((value, i) => Quote(value, $"{field}[{i}]"));

    // Un backslash finale prima delle virgolette le "escapa": "D:\" va scritto "D:\\".
    private static string Quote(string? value, string field)
    {
        var text = ActionConfig.Required(value, field);

        if (text.Contains('"'))
        {
            throw new ActionConfigException($"{field}: le virgolette non sono ammesse");
        }

        return text.EndsWith('\\') ? $"\"{text}\\\"" : $"\"{text}\"";
    }

    private static IEnumerable<string> Tail(string output) =>
        output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(SummaryLines);
}
