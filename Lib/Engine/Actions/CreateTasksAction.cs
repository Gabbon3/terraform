using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using terraform.lib.console;
using terraform.lib.logger;
using terraform.lib.utils;
using static terraform.lib.utils.ProcessUtils;

namespace terraform.lib.engine.actions;

/// <summary>Clona task schedulati remoti sul computer locale conservando cartella, definizione XML e utenza.</summary>
/// <param name="processRunner">Runner per query PowerShell in streaming e registrazione locale con schtasks.exe.</param>
/// <remarks>I task esistenti vengono sovrascritti; tutti i task registrati restano disabilitati a livello generale.</remarks>
public sealed class CreateTasksAction(IProcessRunner processRunner) : IAction
{
    public string ActionType => "CREATE-TASKS";
    public string ActionDescription => "Clona task schedulati remoti, disabilitati e con le credenziali configurate.";

    /// <summary>Mostra configurazione, selezione per nome o percorso e comportamento della sovrascrittura.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
        [Cyan]CREATE-TASKS[/] - Clona task dal server sorgente al computer locale.
        [DarkGray]Esempio di step YAML:[/]
        id: tasks
        type: CREATE-TASKS
        config:
          sourceComputerName: '{SourceComputerName}'
          filter:
            accountPattern: '*svc_*'
            names: ['\Batch\Import']
          credentials:
            'DOMINIO\svc_batch': '{ServicePassword}'
        """);
        Console.WriteLine();
        ConsolePlus.Write("[Cyan]Configurazione e selezione[/]");
        ConsolePlus.WriteList([
            "[Cyan]sourceComputerName[/] e [Cyan]credentials[/]: [Yellow]obbligatori[/]. [Magenta]{SourceComputerName}[/] e [Magenta]{ServicePassword}[/] provengono dalle variables globali.",
            "[Cyan]filter[/]: opzionale; senza filtri si selezionano [Yellow]tutti i task[/].",
            "[Cyan]accountPattern[/]: wildcard PowerShell su [Cyan]Principal.UserId[/].",
            "[Cyan]names[/]: nomi esatti o percorsi completi, senza distinzione di case; non puo' essere vuoto.",
            "[Yellow]Nome semplice[/]: seleziona tutte le omonimie nelle cartelle. Usare il [Magenta]percorso completo[/] per distinguerle.",
            "[Cyan]Entrambi i filtri[/]: devono essere soddisfatti [Yellow]entrambi[/].",
        ]);
        Console.WriteLine();
        ConsolePlus.Write("[Cyan]Comportamento e prerequisiti[/]");
        ConsolePlus.WriteList([
            "[Yellow]Task esistenti: sovrascritti[/]. [Green]Settings/Enabled=false[/]; i trigger restano invariati.",
            "[Cyan]credentials[/]: account cercati ignorando il case; utenza assente: [Yellow]Warning e task saltato[/].",
            "[Yellow]XML vuoto[/]: Warning e skip. [Yellow]XML malformato o registrazione fallita[/]: interrompe lo step.",
            "[Cyan]Dry-run[/]: interroga la sorgente, controlla credenziali nella mappa e XML; mostra cosa verrebbe registrato, [Green]senza modifiche locali[/].",
            "[Yellow]Limiti del dry-run[/]: non verifica la password su Windows ne' la validazione completa di Task Scheduler.",
            "[Yellow]Prerequisiti[/]: Windows, amministratore locale e [Cyan]PowerShell Remoting[/] sul server sorgente con l'identita' che esegue terraform.",
        ]);
    }

    /// <summary>Valida server, filtri e mappa credenziali senza interrogare Windows.</summary>
    public void Validate(JsonElement config) => WindowsClone.ReadConfig(config);

    /// <summary>Esporta i task selezionati e li registra in sequenza sul target locale, disabilitandoli nell'XML.</summary>
    /// <param name="context">Logger, modalita' dry-run e token di annullamento dello step.</param>
    /// <param name="config">Config con server, filtri opzionali e credenziali; token globali gia' risolti.</param>
    /// <returns>Conteggio dei task registrati o da registrare e di quelli saltati; in dry-run include i task trovati.</returns>
    /// <remarks>
    /// Account non mappati e XML vuoti producono Warning e skip.
    /// XML malformati, errori remoti e registrazioni fallite interrompono lo step, senza rollback dei task precedenti.
    /// </remarks>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var settings = WindowsClone.ReadConfig(config);
        context.CancellationToken.ThrowIfCancellationRequested();

        // Stesso flow di Phase3: export e ConvertTo-Json sul server sorgente, una riga per task.
        // I valori configurati sono quotati come letterali; nessuna password viene inviata alla query.
        var psScript = $$"""
            Invoke-Command -ComputerName {{PowerShellLiteral(settings.SourceComputerName)}} -ScriptBlock {
                $ErrorActionPreference = 'Stop'
                $accountPattern = {{PowerShellLiteral(settings.Filter?.AccountPattern)}}
                $names = @({{string.Join(", ", (settings.Filter?.Names ?? []).Select(PowerShellLiteral))}})
                Get-ScheduledTask | Where-Object {
                    ($null -eq $accountPattern -or $_.Principal.UserId -like $accountPattern) -and
                    ($names.Count -eq 0 -or $names -contains $_.TaskName -or $names -contains ($_.TaskPath + $_.TaskName))
                } | ForEach-Object {
                    [PSCustomObject]@{
                        TaskName = $_.TaskName
                        TaskPath = $_.TaskPath
                        Principal = $_.Principal.UserId
                        Xml = Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath
                    } | ConvertTo-Json -Depth 5 -Compress
                }
            }
            """;

        var found = 0;
        var planned = 0;
        var created = 0;
        var skipped = 0;
        await foreach (var line in ExecuteAndStreamPowerShellAsync(processRunner, psScript, context.CancellationToken))
        {
            var task = JsonSerializer.Deserialize<TaskDto>(line, ActionConfig.JsonOptions)
                ?? throw new InvalidOperationException("Task remoto: riga JSON nulla.");
            var name = ActionConfig.Required(task.TaskName, "task remoto: TaskName");
            var path = string.IsNullOrWhiteSpace(task.TaskPath) ? "\\" : task.TaskPath;
            // Il nome semplice non identifica univocamente un task: conserviamo la cartella nei log e nella scrittura.
            var fullName = path + name;
            var account = ActionConfig.Required(task.Principal, $"task {fullName}: Principal");

            found++;
            var hasPassword = WindowsClone.TryGetPassword(context, settings, account, fullName, out var password);

            if (context.IsDryRun)
            {
                context.Logger.Write($"[DRY-RUN] Task {fullName}: account '{account}', credenziale {(hasPassword ? "presente nella mappa" : "assente nella mappa")}, XML {(string.IsNullOrWhiteSpace(task.Xml) ? "vuoto" : "presente")}.", LogLevel.Info);
            }

            if (!hasPassword)
            {
                skipped++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(task.Xml))
            {
                context.Logger.Write($"Task {fullName}: XML vuoto, saltato.", LogLevel.Warn);
                skipped++;
                continue;
            }

            // Nessun fallback all'XML originale se la modifica fallisce: rischieremmo di registrare un task attivo.
            var xml = DisableTask(task.Xml);

            // Stessi controlli della run reale, ma senza file temporanei o processi di registrazione.
            if (context.IsDryRun)
            {
                context.Logger.Write($"[DRY-RUN] Task {fullName}: verrebbe registrato (DISABLED) con utenza '{account}'; XML verificato, Settings/Enabled=false, trigger invariati. Eventuale task locale esistente sovrascritto (/F).", LogLevel.Info);
                planned++;
                continue;
            }

            var xmlPath = Path.GetTempFileName();
            try
            {
                // L'unico file temporaneo e' l'XML richiesto da schtasks, non l'intero export remoto.
                // UTF-16LE corrisponde alla dichiarazione XML impostata da DisableTask.
                await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode, context.CancellationToken);
                var arguments = $"/Create /TN {QuoteArgument(fullName)} /XML {QuoteArgument(xmlPath)} " +
                    $"/RU {QuoteArgument(account)} /RP {QuoteArgument(password)} /F";
                var result = await processRunner.RunAsync("schtasks.exe", arguments, context.CancellationToken);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Registrazione task {fullName}: schtasks.exe exit code {result.ExitCode}");
                }
            }
            finally
            {
                // Anche registrazione fallita o Ctrl+C devono rimuovere l'XML temporaneo.
                File.Delete(xmlPath);
            }
            context.Logger.Write($"Task {fullName}: registrato (DISABLED) con utenza {account}.", LogLevel.Success);
            created++;
        }

        return context.IsDryRun
            ? new ActionResult(true, $"{found} task trovati, {planned} da registrare, {skipped} saltati")
            : new ActionResult(true, $"{created} task registrati, {skipped} saltati");
    }

    /// <summary>Imposta Settings/Enabled=false senza modificare lo stato Enabled dei singoli trigger.</summary>
    /// <param name="xml">Definizione esportata da Task Scheduler.</param>
    /// <returns>XML con dichiarazione UTF-16, da salvare con la stessa codifica durante la registrazione.</returns>
    /// <exception cref="System.Xml.XmlException">Documento XML malformato.</exception>
    /// <exception cref="InvalidOperationException">Elemento radice Task assente.</exception>
    /// <remarks>Usa il namespace della radice e crea Settings o Enabled se mancanti; non ripiega sull'XML originale.</remarks>
    private static string DisableTask(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Task")
        {
            throw new InvalidOperationException("XML task non valido: radice Task mancante");
        }
        // Task Scheduler usa normalmente un namespace: cercare "Settings" senza ns non troverebbe il nodo.
        var ns = root.Name.Namespace;
        var settings = root.Element(ns + "Settings");
        if (settings is null)
        {
            settings = new XElement(ns + "Settings");
            // Nello schema Task Scheduler, Settings precede Data e Actions: non aggiungerlo sempre in coda.
            var afterSettings = root.Elements().FirstOrDefault(element => element.Name.LocalName is "Data" or "Actions");
            if (afterSettings is null)
                root.Add(settings);
            else
                afterSettings.AddBeforeSelf(settings);
        }
        // Solo il figlio diretto di Settings, non tutti i nodi Enabled (che includerebbero quelli dei trigger).
        settings.SetElementValue(ns + "Enabled", "false");
        document.Declaration = new XDeclaration("1.0", "utf-16", null);
        return document.Declaration + Environment.NewLine + document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Identita' e XML del task esportato, senza i metadati di trasporto del remoting.</summary>
    private sealed record TaskDto(string? TaskName, string? TaskPath, string? Principal, string? Xml);
}
