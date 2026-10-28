using System.Collections.Frozen;
using System.Text.Json;
using terraform.lib.console;
using terraform.lib.utils;

namespace terraform.lib.engine.actions;

/// <summary>
/// Azione <c>MSI-INSTALL</c>: installa in sequenza i pacchetti <c>.msi</c> di <c>paths</c> con <c>msiexec.exe</c>.
/// </summary>
/// <remarks>
/// Config: <c>paths</c>, <c>msiArguments</c> (default <c>/i "{MsiPath}" /qn /norestart</c>), <c>successExitCodes</c> (default <c>[0, 3010]</c>)
/// e <c>ignoreIfNotFound</c>. <c>{MsiPath}</c> non è una variabile di <c>variables</c>: è ogni percorso dell'array <c>paths</c>, preso uno alla volta.
/// Lo risolve quest'azione per ogni pacchetto e solo in <c>msiArguments</c>, mentre gli altri placeholder sono già risolti dal runner.
/// Rispetto a un exe, qui 1638 (già installato) non è un successo.
/// </remarks>
public sealed class MsiInstallAction(IProcessRunner processRunner) : IAction
{
    private const string DefaultArguments = "/i \"{MsiPath}\" /qn /norestart";

    private static readonly IReadOnlySet<string> MsiTokens = new[] { "MsiPath" }.ToFrozenSet();

    // 0 = successo, 3010 = riavvio richiesto.
    private static readonly IReadOnlySet<int> BuiltInSuccessExitCodes = new[] { 0, 3010 }.ToFrozenSet();

    private sealed class Config
    {
        public List<string>? Paths { get; init; }
        public string? MsiArguments { get; init; }
        public List<int>? SuccessExitCodes { get; init; }
        public bool IgnoreIfNotFound { get; init; }
    }

    public string ActionType => "MSI-INSTALL";
    public string ActionDescription => "Installa in sequenza pacchetti MSI usando msiexec.exe.";

    /// <summary>Mostra configurazione MSI, token per pacchetto e codici di successo.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
                [Cyan]MSI-INSTALL[/] - Installa pacchetti MSI in sequenza tramite msiexec.exe.
                [DarkGray]Esempio di step (JSON con commenti):[/]
                {
                    "id": "frameworks", // Obbligatorio e univoco.
                    "type": "MSI-INSTALL", // Obbligatorio.
                    "name": "Frameworks", // Opzionale: etichetta per i log.
                    "enabled": true, // Opzionale, default true.
                    "config": {
                        "paths": ["D:\\install\\framework.msi"], // Obbligatorio: almeno un percorso completo .msi, in ordine.
                        "msiArguments": "/i \"{MsiPath}\" /qn /norestart", // Opzionale: questi sono i default; {MsiPath} obbligatorio.
                        "successExitCodes": [0, 3010], // Opzionale: questi sono i default; lista non vuota.
                        "ignoreIfNotFound": false // Opzionale, default false; true salta i pacchetti mancanti con Warn.
                    }
                }
                """);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Configurazione e comportamento");
        ConsolePlus.WriteList([
            "[Cyan]paths[/]: [Yellow]obbligatorio[/], almeno un percorso completo [Cyan].msi[/]; installazione [Green]in ordine[/].",
            "[Cyan]msiArguments[/]: default [Green]/i \"{MsiPath}\" /qn /norestart[/]; deve contenere [Magenta]{MsiPath}[/].",
            "[Magenta]{MsiPath}[/]: percorso del pacchetto corrente, valido [Yellow]solo in msiArguments[/]; non definirlo in variables.",
            "[Magenta]{Nome}[/]: gli altri token provengono dalle [Cyan]variables[/] globali.",
            "[Cyan]/i[/] installa, [Cyan]/qn[/] nasconde la UI, [Cyan]/norestart[/] impedisce il riavvio automatico.",
            "[Cyan]successExitCodes[/]: default [Green][0, 3010][/], lista non vuota; [Green]3010[/] = riavvio richiesto. [Yellow]1638 non e' successo[/] salvo override.",
            "[Yellow]File obbligatorio mancante[/]: nessun processo avviato; [Cyan]ignoreIfNotFound: true[/] lo salta con Warning.",
            "[Yellow]Primo fallimento[/]: interrompe la sequenza.",
            "[Cyan]Dry-run[/]: mostra i comandi [Green]senza avviarli[/]; tollera pacchetti non ancora presenti."
        ]);
    }

    // Dichiarato perché il runner lasci intatto {MsiPath} nel pre-flight, altrimenti lo segnalerebbe come variabile sconosciuta.
    public IReadOnlySet<string> ItemTokens => MsiTokens;

    /// <summary>
    /// Valida l'intera config e prepara un comando msiexec per pacchetto, poi li esegue in ordine (vedi <see cref="InstallSequence"/>).
    /// </summary>
    public Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
        => InstallSequence.RunAsync(context, processRunner, PrepareItems(config, context.Variables));

    /// <summary>Valida percorsi, exit code e template senza leggere pacchetti MSI.</summary>
    public void Validate(JsonElement config) => PrepareItems(config, new VariableEngine());

    /// <summary>Valida percorsi e template senza leggere pacchetti; risolve solo il token per-item rimasto dopo il preflight.</summary>
    private static List<InstallItem> PrepareItems(JsonElement config, VariableEngine variables)
    {
        var settings = ActionConfig.Read<Config>(config);

        if (settings.Paths is not { Count: > 0 })
        {
            throw new ActionConfigException("paths: serve almeno un percorso");
        }

        var template = settings.MsiArguments ?? DefaultArguments;

        // Senza {MsiPath} msiexec non saprebbe quale pacchetto installare.
        if (!VariableEngine.ContainsToken(template, MsiTokens))
        {
            throw new ActionConfigException("msiArguments: deve contenere il token {MsiPath}");
        }

        var codes = InstallSequence.ParseExitCodes(settings.SuccessExitCodes, BuiltInSuccessExitCodes, "successExitCodes");
        var items = new List<InstallItem>();

        for (var i = 0; i < settings.Paths.Count; i++)
        {
            var field = $"paths[{i}]";

            // Il runner lascia {MsiPath} intatto in tutta la config: nei percorsi resterebbe testo letterale.
            if (settings.Paths[i] is { } raw && VariableEngine.ContainsToken(raw, MsiTokens))
            {
                throw new ActionConfigException($"{field}: il token {{MsiPath}} è ammesso solo in msiArguments");
            }

            var path = ActionConfig.FullPath(settings.Paths[i], field);

            if (!string.Equals(Path.GetExtension(path), ".msi", StringComparison.OrdinalIgnoreCase))
            {
                throw new ActionConfigException($"{field}: il file deve essere un .msi, trovato '{path}'");
            }

            items.Add(new InstallItem(
                Name: Path.GetFileName(path),
                SourcePath: path,
                FileName: "msiexec.exe",
                Arguments: variables.Resolve(template, new Dictionary<string, string> { ["MsiPath"] = path }),
                SuccessExitCodes: codes,
                IgnoreIfNotFound: settings.IgnoreIfNotFound));
        }

        return items;
    }
}
