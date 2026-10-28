using System.Text.Json;
using terraform.lib.console;
using terraform.lib.logger;
using terraform.lib.utils;
using static terraform.lib.utils.ProcessUtils;

namespace terraform.lib.engine.actions;

/// <summary>Clona servizi Windows remoti sul computer locale, mantenendo comando e utenza originali.</summary>
/// <param name="processRunner">Runner per query PowerShell in streaming e comandi sc.exe locali.</param>
/// <remarks>Non aggiorna i servizi esistenti e non avvia quelli nuovi, creati sempre con startup Disabled.</remarks>
public sealed class CreateServicesAction(IProcessRunner processRunner) : IAction
{
    public string ActionType => "CREATE-SERVICES";
    public string ActionDescription => "Clona servizi Windows remoti, disabilitati e con le credenziali configurate.";

    /// <summary>Mostra configurazione, semantica dei filtri e prerequisiti dell'azione.</summary>
    public void Help()
    {
        ConsolePlus.Write("""
        [Cyan]CREATE-SERVICES[/] - Clona servizi dal server sorgente al computer locale.
        [DarkGray]Esempio di step (JSON):[/]
        {
            "id": "services",
            "type": "CREATE-SERVICES",
            "config": {
                "sourceComputerName": "{SourceComputerName}",
                "filter": {
                    "accountPattern": "*svc_*",
                    "names": ["ServizioBatch"]
                },
                "credentials": {
                    "DOMINIO\\svc_batch": "{ServicePassword}"
                }
            }
        }
        """);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Configurazione e selezione");
        ConsolePlus.WriteList([
            "[Cyan]sourceComputerName[/] e [Cyan]credentials[/]: [Yellow]obbligatori[/]. [Magenta]{SourceComputerName}[/] e [Magenta]{ServicePassword}[/] provengono dalle variables globali.",
            "[Cyan]filter[/]: opzionale; senza filtri si selezionano [Yellow]tutti i servizi[/].",
            "[Cyan]accountPattern[/]: wildcard PowerShell sull'account. [Cyan]names[/]: nomi esatti del servizio ([Yellow]non DisplayName[/]), senza distinzione di case.",
            "[Cyan]Entrambi i filtri[/]: devono essere soddisfatti [Yellow]entrambi[/]. [Cyan]names[/] non puo' essere vuoto.",
        ]);
        Console.WriteLine();
        ConsolePlus.WriteBoxHeader("Comportamento e prerequisiti");
        ConsolePlus.WriteList([
            "[Green]Servizi esistenti[/]: saltati. Nuovi servizi: sempre [Green]Disabled[/], senza avviarli.",
            "[Cyan]credentials[/]: account cercati ignorando il case; utenza assente: [Yellow]Warning e servizio saltato[/].",
            "[Green]Comando binario conservato[/], inclusi virgolette e argomenti. Eseguibile mancante: [Yellow]Warning[/], creazione comunque consentita.",
            "[Cyan]Dry-run[/]: interroga la sorgente e verifica esistenza, credenziali nella mappa e percorso; mostra cosa verrebbe creato, [Green]senza modifiche locali[/].",
            "[Yellow]Credenziale presente non significa password valida[/]: il dry-run non verifica l'autenticazione dell'utenza.",
            "[Yellow]Prerequisiti[/]: Windows, amministratore locale e accesso [Cyan]CIM[/] al server sorgente con l'identita' che esegue terraform.",
        ]);
    }

    /// <summary>Valida server, filtri e mappa credenziali senza interrogare Windows.</summary>
    public void Validate(JsonElement config) => WindowsClone.ReadConfig(config);

    /// <summary>Valida, esporta i servizi selezionati e crea in sequenza quelli mancanti sul target locale.</summary>
    /// <param name="context">Logger, modalita' dry-run e token di annullamento dello step.</param>
    /// <param name="config">Config con server, filtri opzionali e mappa credenziali; token globali gia' risolti.</param>
    /// <returns>Conteggio dei servizi creati o da creare e di quelli saltati; in dry-run include i servizi trovati.</returns>
    /// <remarks>
    /// Servizi esistenti e account non mappati producono Warning e skip.
    /// Gli altri errori risalgono alla pipeline interrompendo la sequenza, senza rollback dei servizi gia' creati.
    /// </remarks>
    public async Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var settings = WindowsClone.ReadConfig(config);
        context.CancellationToken.ThrowIfCancellationRequested();

        // Come in Phase3: uno script produce una riga JSON per servizio, elaborata subito nel ciclo.
        // I valori di config sono letterali PowerShell, non frammenti di codice; le password non entrano nella query.
        var psScript = $$"""
            $accountPattern = {{PowerShellLiteral(settings.Filter?.AccountPattern)}}
            $names = @({{string.Join(", ", (settings.Filter?.Names ?? []).Select(PowerShellLiteral))}})
            Get-CimInstance Win32_Service -ComputerName {{PowerShellLiteral(settings.SourceComputerName)}} |
                Where-Object {
                    ($null -eq $accountPattern -or $_.StartName -like $accountPattern) -and
                    ($names.Count -eq 0 -or $names -contains $_.Name)
                } | Select-Object Name, DisplayName, PathName, StartName |
                ForEach-Object { $_ | ConvertTo-Json -Depth 5 -Compress }
            """;
        
        var found = 0;
        var planned = 0;
        var created = 0;
        var skipped = 0;
        await foreach (var line in ExecuteAndStreamPowerShellAsync(processRunner, psScript, context.CancellationToken))
        {
            var service = JsonSerializer.Deserialize<ServiceDto>(line, ActionConfig.JsonOptions)
                ?? throw new InvalidOperationException("Servizio remoto: riga JSON nulla.");
            var name = ActionConfig.Required(service.Name, "servizio remoto: Name");

            found++;

            var existing = await processRunner.RunAsync("sc.exe", $"query {QuoteArgument(name)}", context.CancellationToken);
            if (existing.ExitCode == 0)
            {
                context.Logger.Write($"Servizio {name}: esiste gia', saltato.", LogLevel.Warn);
                skipped++;
                continue;
            }
            // 1060 indica un servizio inesistente; accesso negato o altri errori non sono uno skip.
            if (existing.ExitCode != 1060)
            {
                throw new InvalidOperationException($"Verifica servizio {name}: sc.exe exit code {existing.ExitCode}");
            }

            // Controllo dopo lo skip degli esistenti: non richiediamo credenziali per servizi che non tocchiamo.
            // Nessun fallback a LocalSystem se manca la password dell'utenza originale.
            var account = ActionConfig.Required(service.StartName, $"servizio {name}: StartName");
            if (!WindowsClone.TryGetPassword(context, settings, account, name, out var password))
            {
                skipped++;
                continue;
            }
            var binaryPath = ActionConfig.Required(service.PathName, $"servizio {name}: PathName");
            // Il percorso estratto serve solo all'avviso; sc.exe riceve il comando originale, senza togliere virgolette.
            var exePath = ExtractExePath(binaryPath);
            if (!File.Exists(exePath))
            {
                context.Logger.Write($"Servizio {name}: eseguibile '{exePath}' non presente sul target; verra' creato comunque.", LogLevel.Warn);
            }

            var displayName = string.IsNullOrWhiteSpace(service.DisplayName) ? name : service.DisplayName;

            // Il dry-run esegue le stesse verifiche, fermandosi prima del comando che modifica il target.
            if (context.IsDryRun)
            {
                context.Logger.Write($"[DRY-RUN] Servizio {name} ('{displayName}'): verrebbe creato (DISABLED) con utenza '{account}', credenziale presente nella mappa, binPath: {binaryPath}", LogLevel.Info);
                planned++;
                continue;
            }

            // Gli spazi dopo '=' sono richiesti da sc.exe. Impostiamo anche l'utenza nella creazione,
            // evitando un servizio parzialmente configurato se una successiva sc config dovesse fallire.
            var arguments = $"create {QuoteArgument(name)} binPath= {QuoteArgument(binaryPath)} " +
                $"DisplayName= {QuoteArgument(displayName)} start= disabled obj= {QuoteArgument(account)} password= {QuoteArgument(password)}";
            var result = await processRunner.RunAsync("sc.exe", arguments, context.CancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Creazione servizio {name}: sc.exe exit code {result.ExitCode}");
            }
            context.Logger.Write($"Servizio {name}: creato (DISABLED) con utenza {account}.", LogLevel.Success);
            created++;
        }

        return context.IsDryRun
            ? new ActionResult(true, $"{found} servizi trovati, {planned} da creare, {skipped} saltati")
            : new ActionResult(true, $"{created} servizi creati, {skipped} saltati");
    }

    /// <summary>Estrae il probabile eseguibile dal comando del servizio, solo per verificarne la presenza locale.</summary>
    /// <param name="path">PathName remoto completo, eventualmente con virgolette e argomenti.</param>
    /// <returns>Contenuto delle prime virgolette, percorso fino a .exe o, come ultimo tentativo, primo token.</returns>
    /// <remarks>Euristica diagnostica, non parser della command line: il valore restituito non modifica BinaryPathName.</remarks>
    private static string ExtractExePath(string path)
    {
        var value = path.Trim();
        if (value.StartsWith('"'))
        {
            var quote = value.IndexOf('"', 1);
            if (quote > 1)
            {
                return value[1..quote];
            }
        }
        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? value[..(exe + 4)] : value.Split(' ', 2)[0];
    }

    /// <summary>Soli campi esportati da Win32_Service; lo stato e lo startup sorgenti non vengono replicati.</summary>
    private sealed record ServiceDto(string? Name, string? DisplayName, string? PathName, string? StartName);
}
