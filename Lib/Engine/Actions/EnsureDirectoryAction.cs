using System.Text.Json;
using terraform.lib.console;
using terraform.lib.logger;

namespace terraform.lib.engine.actions;

/// <summary>
/// Azione <c>REQUIRED-FOLDERS</c>: crea le cartelle elencate in <c>paths</c>, genitori compresi.
/// </summary>
/// <remarks>
/// È idempotente: una cartella già presente non viene toccata. I percorsi devono essere completi.
/// Se uno dei percorsi esiste come file l'azione fallisce senza creare nulla.
/// </remarks>
public sealed class EnsureDirectoryAction : IAction
{
    private sealed class Config
    {
        public List<JsonElement>? Paths { get; init; }
    }

    private sealed record DirectorySpec
    {
        public string? Path { get; init; }
        public string? Source { get; init; }
        public int Depth { get; init; }
    }

    public string ActionType => "REQUIRED-FOLDERS";
    public string ActionDescription => "Crea le cartelle configurate e i relativi genitori mancanti.";

    /// <summary>Mostra esempio di configurazione e regole operative dell'azione.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
                [Cyan]REQUIRED-FOLDERS[/] - Crea cartelle e genitori mancanti; non modifica quelle esistenti.
                [DarkGray]Esempio di step YAML:[/]
                id: folders # Obbligatorio e univoco nella pipeline.
                type: REQUIRED-FOLDERS # Obbligatorio.
                name: Cartelle # Opzionale: etichetta per i log.
                enabled: true # Opzionale, default true.
                config:
                  paths: # Obbligatorio: almeno un percorso completo.
                    - 'D:\data'
                    - path: '{RootDir}\logs'
                        source: '\\{SourceServer}\D$\logs'
                        depth: 1
                """);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Configurazione e comportamento");
        ConsolePlus.WriteList([
            "[Cyan]paths[/]: [Yellow]obbligatorio[/], almeno un percorso completo; crea anche i genitori mancanti.",
            "Ogni voce puo' essere una stringa oppure un oggetto con [Cyan]path[/], [Cyan]source[/] e [Cyan]depth[/].",
            "[Cyan]depth[/]: 0 (default) crea solo path; 1 include le sottocartelle immediate; -1 replica tutto l'albero. Per depth diverso da 0 serve source.",
            "Si replicano solo cartelle, mai file; junction e link simbolici non vengono seguiti. La sorgente viene letta anche in dry-run.",
            "[Magenta]{RootDir}[/]: va definita nelle [Cyan]variables[/] globali della pipeline.",
            "[Green]Cartelle esistenti[/]: lasciate invariate. [Yellow]File omonimo[/]: lo step fallisce prima di creare cartelle.",
            "[Cyan]Dry-run[/]: mostra le cartelle da creare, [Green]senza crearle[/]."
        ]);
    }

    /// <summary>Valida i percorsi senza verificarne l'esistenza.</summary>
    public void Validate(JsonElement config) => ReadPaths(config);

    /// <summary>Normalizza i percorsi senza verificarne l'esistenza, condividendo la validazione con il preflight.</summary>
    private static List<DirectorySpec> ReadPaths(JsonElement config)
    {
        var settings = ActionConfig.Read<Config>(config);
        if (settings.Paths is not { Count: > 0 })
        {
            throw new ActionConfigException("paths: serve almeno un percorso");
        }
        return settings.Paths.Select((entry, index) =>
        {
            var field = $"paths[{index}]";
            DirectorySpec spec;
            if (entry.ValueKind == JsonValueKind.String)
            {
                spec = new DirectorySpec { Path = entry.GetString() };
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    spec = ActionConfig.Read<DirectorySpec>(entry);
                }
                catch (ActionConfigException ex)
                {
                    throw new ActionConfigException($"{field}: {ex.Message}");
                }
            }
            else
            {
                throw new ActionConfigException($"{field}: serve un percorso o un oggetto con path, source e depth");
            }

            if (spec.Depth < -1)
                throw new ActionConfigException($"{field}.depth: deve essere -1 oppure maggiore o uguale a 0");

            var target = Path.TrimEndingDirectorySeparator(ActionConfig.FullPath(spec.Path, $"{field}.path"));
            var source = spec.Source is not null || spec.Depth != 0
                ? Path.TrimEndingDirectorySeparator(ActionConfig.FullPath(spec.Source, $"{field}.source"))
                : null;
            return spec with { Path = target, Source = source };
        }).ToList();
    }

    /// <summary>Valida tutti i percorsi, poi crea quelli mancanti; in dry-run si limita a loggare cosa creerebbe.</summary>
    /// <remarks>Un file omonimo restituisce un risultato negativo: e' uno stato del disco, non un errore di config.</remarks>
    public Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var paths = ReadPaths(config);

        // Si valida tutto prima di creare qualcosa, così un errore non lascia la pipeline a metà.
        // Dopo aver tolto il separatore finale "D:\a" e "D:\a\" sono lo stesso percorso: `seen` evita di contarlo due volte.
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        for (var i = 0; i < paths.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var spec = paths[i];
            foreach (var path in EnumerateTargets(spec))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    errors.Add($"paths[{i}]: esiste un file con questo nome: {path}");
                }
                else if (seen.Add(path))
                {
                    targets.Add(path);
                }
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(new ActionResult(false, string.Join("; ", errors)));
        }

        var created = 0;
        var existing = 0;

        foreach (var path in targets)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                existing++;
                continue;
            }

            created++;

            // In dry-run "created" conta quelle da creare, ed è il motivo per cui il messaggio finale cambia verbo.
            if (context.IsDryRun)
            {
                context.Logger.Write($"[DRY-RUN] creerebbe {path}", LogLevel.Info);
                continue;
            }

            Directory.CreateDirectory(path);
            context.Logger.Write($"creata {path}", LogLevel.Info);
        }

        var verb = context.IsDryRun ? "da creare" : "create";
        return Task.FromResult(new ActionResult(true, $"{created} {verb}, {existing} già presenti"));
    }

    private static IEnumerable<string> EnumerateTargets(DirectorySpec spec)
    {
        yield return spec.Path!;
        if (spec.Depth == 0)
            yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = spec.Depth == -1 ? int.MaxValue : spec.Depth - 1,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var directory in Directory.EnumerateDirectories(spec.Source!, "*", options))
        {
            yield return Path.Combine(spec.Path!, Path.GetRelativePath(spec.Source!, directory));
        }
    }
}
