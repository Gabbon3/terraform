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
        public List<string>? Paths { get; init; }
    }

    public string ActionType => "REQUIRED-FOLDERS";
    public string ActionDescription => "Crea le cartelle configurate e i relativi genitori mancanti.";

        public void Help() => ConsolePlus.Write("""
                [Cyan]REQUIRED-FOLDERS[/] - Crea cartelle e genitori mancanti; non modifica quelle esistenti.
                Esempio di step (JSON con commenti):
                {
                    "id": "folders", // Obbligatorio e univoco nella pipeline.
                    "type": "REQUIRED-FOLDERS", // Obbligatorio.
                    "name": "Cartelle", // Opzionale: etichetta per i log.
                    "enabled": true, // Opzionale, default true.
                    "config": {
                        "paths": ["D:\\data", "{RootDir}\\logs"] // Obbligatorio: almeno un percorso completo.
                    }
                }
                {RootDir} va definita in variables della pipeline. Un file omonimo fa fallire lo step.
                Dry-run: mostra le cartelle da creare senza crearle.
                """);

    /// <summary>
    /// Valida tutti i percorsi, poi crea quelli mancanti; in dry-run si limita a loggare cosa creerebbe.
    /// </summary>
    /// <remarks>
    /// Restituisce <c>ActionResult</c> negativo (e non un'eccezione) per un file con lo stesso nome, perché è uno stato del disco e non un errore di config.
    /// Il lavoro è sincrono: non c'è I/O asincrono da attendere, quindi il risultato è già completato.
    /// </remarks>
    public Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var settings = ActionConfig.Read<Config>(config);

        if (settings.Paths is not { Count: > 0 })
        {
            throw new ActionConfigException("paths: serve almeno un percorso");
        }

        // Si valida tutto prima di creare qualcosa, così un errore non lascia la pipeline a metà.
        // Dopo aver tolto il separatore finale "D:\a" e "D:\a\" sono lo stesso percorso: `seen` evita di contarlo due volte.
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        for (var i = 0; i < settings.Paths.Count; i++)
        {
            var path = Path.TrimEndingDirectorySeparator(ActionConfig.FullPath(settings.Paths[i], $"paths[{i}]"));

            if (File.Exists(path))
            {
                errors.Add($"paths[{i}]: esiste un file con questo nome: {path}");
            }
            else if (seen.Add(path))
            {
                targets.Add(path);
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
}
