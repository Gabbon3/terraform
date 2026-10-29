using System.Collections.Frozen;
using System.Text.Json;
using terraform.lib.console;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.lib.engine.actions;

/// <summary>
/// Azione <c>EXECUTE-PROCESS</c>: esegue in sequenza una lista di programmi, tipicamente installer <c>.exe</c>.
/// </summary>
/// <remarks>
/// Config: <c>items</c> (<c>path</c>, <c>name</c>, <c>arguments</c>, <c>successExitCodes</c>, <c>ignoreIfNotFound</c>),
/// <c>defaultArguments</c>, <c>defaultSuccessExitCodes</c> (default <c>[0, 3010, 1638]</c>) e <c>manualStepsWarning</c>.
/// Gli argomenti di un item, anche vuoti, hanno la precedenza su <c>defaultArguments</c>; lo stesso vale per i codici di uscita.
/// I placeholder <c>{Nome}</c> sono già risolti dal runner prima dell'esecuzione.
/// </remarks>
public sealed class ExecuteProcessAction(IProcessRunner processRunner) : IAction
{
    // 3010 = riavvio richiesto, 1638 = versione già installata: per un installer sono esiti accettabili.
    private static readonly IReadOnlySet<int> BuiltInSuccessExitCodes = new[] { 0, 3010, 1638 }.ToFrozenSet();

    private sealed class Config
    {
        public List<ItemConfig?>? Items { get; init; }
        public string? DefaultArguments { get; init; }
        public List<int>? DefaultSuccessExitCodes { get; init; }
        public string? ManualStepsWarning { get; init; }
    }

    private sealed class ItemConfig
    {
        public string? Name { get; init; }
        public string? Path { get; init; }
        public string? Arguments { get; init; }
        public List<int>? SuccessExitCodes { get; init; }
        public bool IgnoreIfNotFound { get; init; }
    }

    public string ActionType => "EXECUTE-PROCESS";
    public string ActionDescription => "Esegue in sequenza programmi o installer con argomenti e codici di successo configurabili.";

    /// <summary>Mostra configurazione degli installer, precedenza dei default e gestione dei fallimenti.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
                [Cyan]EXECUTE-PROCESS[/] - Esegue programmi in sequenza; si ferma al primo fallimento.
                [DarkGray]Esempio di step YAML:[/]
                id: software # Obbligatorio e univoco.
                type: EXECUTE-PROCESS # Obbligatorio.
                name: Software # Opzionale: etichetta per i log.
                enabled: true # Opzionale, default true.
                config:
                  defaultArguments: '/quiet /norestart' # Opzionale, default vuoto.
                  defaultSuccessExitCodes: [0, 3010, 1638] # Opzionale: questi sono i default; lista non vuota.
                  manualStepsWarning: Completare i passaggi manuali. # Opzionale: avviso finale solo su successo.
                  items: # Obbligatorio: almeno un elemento, eseguiti nell'ordine indicato.
                    - path: 'D:\install\setup.exe' # Obbligatorio: percorso completo.
                      name: Setup # Opzionale, default nome del file.
                      arguments: '/install /quiet /norestart' # Opzionale: sostituisce defaultArguments, anche se vuoto.
                      successExitCodes: [0, 3010] # Opzionale: sostituisce i default; lista non vuota.
                      ignoreIfNotFound: false # Opzionale, default false; true salta il file con Warn.
                """);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Configurazione e comportamento");
        ConsolePlus.WriteList([
            "[Cyan]items[/]: [Yellow]obbligatorio[/], almeno un elemento con [Cyan]path[/] completo; esecuzione [Green]in ordine[/].",
            "[Cyan]arguments[/] e [Cyan]successExitCodes[/] dell'item sostituiscono i rispettivi default; anche arguments vuoto ha precedenza.",
            "[Cyan]defaultArguments[/]: default vuoto. [Cyan]defaultSuccessExitCodes[/]: [Green][0, 3010, 1638][/]; le liste di codici non possono essere vuote.",
            "[Green]3010[/] = riavvio richiesto; [Green]1638[/] = versione gia' installata.",
            "[Magenta]{Nome}[/]: variabile globale definita in [Cyan]variables[/]. [Yellow]Nessuna shell implicita[/].",
            "[Yellow]File obbligatorio mancante[/]: fallisce prima di avviare programmi; [Cyan]ignoreIfNotFound: true[/] lo salta con Warning.",
            "[Yellow]Primo fallimento[/]: interrompe la sequenza. [Cyan]manualStepsWarning[/] compare solo se lo step riesce.",
            "[Cyan]Dry-run[/]: mostra i comandi, [Green]senza avviare processi[/]; file non ancora presenti segnalati come avvisi."
        ]);
    }

    /// <summary>
    /// Valida l'intera config, poi esegue gli item in ordine (vedi <see cref="InstallSequence"/>).
    /// </summary>
    /// <remarks>
    /// Il messaggio <c>manualStepsWarning</c> compare solo se lo step riesce: è un promemoria per ciò che resta da fare a mano dopo.
    /// </remarks>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var settings = ActionConfig.Read<Config>(config);
        var items = PrepareItems(settings);
        var result = await InstallSequence.RunAsync(context, processRunner, items);

        if (result.Success && !string.IsNullOrWhiteSpace(settings.ManualStepsWarning))
        {
            context.Logger.Write(settings.ManualStepsWarning, LogLevel.Warn);
        }

        return result;
    }

    /// <summary>Valida gli item e gli exit code senza verificare la presenza degli eseguibili.</summary>
    public void Validate(JsonElement config) => PrepareItems(ActionConfig.Read<Config>(config));

    /// <summary>Valida e prepara i comandi senza controllare file che potrebbero essere creati da step precedenti.</summary>
    private static List<InstallItem> PrepareItems(Config settings)
    {
        if (settings.Items is not { Count: > 0 })
        {
            throw new ActionConfigException("items: serve almeno un elemento");
        }

        var defaultCodes = InstallSequence.ParseExitCodes(settings.DefaultSuccessExitCodes, BuiltInSuccessExitCodes, "defaultSuccessExitCodes");
        var items = new List<InstallItem>();

        for (var i = 0; i < settings.Items.Count; i++)
        {
            var item = settings.Items[i] ?? throw new ActionConfigException($"items[{i}]: elemento nullo");
            var path = ActionConfig.FullPath(item.Path, $"items[{i}].path");

            items.Add(new InstallItem(
                Name: string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileName(path) : item.Name,
                SourcePath: path,
                FileName: path,
                Arguments: item.Arguments ?? settings.DefaultArguments ?? "",
                SuccessExitCodes: InstallSequence.ParseExitCodes(item.SuccessExitCodes, defaultCodes, $"items[{i}].successExitCodes"),
                IgnoreIfNotFound: item.IgnoreIfNotFound));
        }

        return items;
    }
}
