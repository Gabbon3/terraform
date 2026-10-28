using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using terraform.lib.config;
using terraform.lib.console;

namespace terraform.Migrator;

// ==============================================================================
// Comando CLI per il Migrator usando Spectre.Console
// ==============================================================================

public sealed class MigratorSettings : CommandSettings
{
    [Description("Nome del server sorgente (es. TEBTCH04)")]
    [CommandOption("-s|--source-server <SERVER>")]
    public string? SourceServer { get; init; }

    [Description("Nome del computer sorgente per connessione remota (es. VM1-3LW11N6-29)")]
    [CommandOption("-c|--source-computer <COMPUTER>")]
    public string? SourceComputerName { get; init; }

    [Description("IP del nuovo server target (es. 10.20.51.200)")]
    [CommandOption("-t|--target-ip <TARGET_IP>")]
    public string? TargetIp { get; init; }

    [Description("Inizia da uno step specifico (Robocopy|SoftwareToInstall|InstallMsi|RegisterDll|CreateServices|CreateTasks|UpdateCleaningConfig|EditHost)")]
    [CommandOption("--start-from <STEP>")]
    public string? StartFrom { get; init; }

    [Description("Esegui solo uno step specifico (Robocopy|SoftwareToInstall|InstallMsi|RegisterDll|CreateServices|CreateTasks|UpdateCleaningConfig|EditHost)")]
    [CommandOption("--run-only <STEP>")]
    public string? RunOnly { get; init; }

    [Description("Modalità dry-run: simula le operazioni senza eseguirle")]
    [CommandOption("--dry-run")]
    [DefaultValue(false)]
    public bool DryRun { get; init; }

    [Description("Inizializza un file migrator_config.json di esempio nella cartella dell'eseguibile con valori template per database, credenziali servizi e percorsi robocopy")]
    [CommandOption("--init-config")]
    [DefaultValue(false)]
    public bool InitConfig { get; init; }

    public override ValidationResult Validate()
    {
        // Se --init-config è attivo, ignora gli altri parametri obbligatori
        if (InitConfig)
        {
            return ValidationResult.Success();
        }

        if (string.IsNullOrWhiteSpace(SourceServer))
        {
            return ValidationResult.Error("Il parametro --source-server è obbligatorio");
        }

        if (string.IsNullOrWhiteSpace(SourceComputerName))
        {
            return ValidationResult.Error("Il parametro --source-computer è obbligatorio");
        }

        if (string.IsNullOrWhiteSpace(TargetIp))
        {
            return ValidationResult.Error("Il parametro --target-ip è obbligatorio");
        }

        // Valida StartFrom e RunOnly
        var validSteps = MigratorConfig.AvailableSteps;

        if (StartFrom != null && !validSteps.Contains(StartFrom, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationResult.Error($"Step non valido in --start-from. Valori validi: {string.Join(", ", validSteps)}");
        }

        if (RunOnly != null && !validSteps.Contains(RunOnly, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationResult.Error($"Step non valido in --run-only. Valori validi: {string.Join(", ", validSteps)}");
        }

        return ValidationResult.Success();
    }
}

public sealed class MigratorCommand : AsyncCommand<MigratorSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MigratorSettings settings, CancellationToken cancellationToken)
    {
        // Banner ASCII art
        PrintBanner();

        // Gestisci flag --init-config
        if (settings.InitConfig)
        {
            return await HandleInitConfigAsync();
        }

        // Configurazione
        var config = new MigratorConfig
        {
            SourceServer = settings.SourceServer!,
            SourceComputerName = settings.SourceComputerName!,
            TargetIp = settings.TargetIp!,
            StartFrom = settings.StartFrom,
            RunOnly = settings.RunOnly,
            DryRun = settings.DryRun
        };

        // Orchestrator
        var orchestrator = new MigratorOrchestrator(config);
        var success = await orchestrator.RunAsync();

        return success ? 0 : 1;
    }

    /// <summary>
    /// Gestisce l'inizializzazione del file di configurazione migrator_config.json.
    /// </summary>
    /// <returns>Exit code: 0 se riuscito, 1 se fallito.</returns>
    private static async Task<int> HandleInitConfigAsync()
    {
        try
        {
            var configPath = await ConfigManager.Instance.InitializeAsync();
            
            ConsolePlus.WriteHr(70);
            Console.WriteLine();
            ConsolePlus.Write("[Green]✓ File di configurazione creato con successo[/]");
            Console.WriteLine();
            
            ConsolePlus.Write("[Yellow]Percorso del file:[/]");
            ConsolePlus.Write($"[Cyan]{configPath}[/]");
            Console.WriteLine();
            
            ConsolePlus.Write("[Yellow]Prossimi passi:[/]");
            ConsolePlus.Write("1. Apri il file migrator_config.json con un editor di testo");
            ConsolePlus.Write("2. Configura la sezione [Cyan]config.database[/]");
            ConsolePlus.Write("   - Imposta [Cyan]nimaster[/] e [Cyan]pulizie[/] con i parametri SQL Server");
            ConsolePlus.Write("3. Configura la sezione [Cyan]steps[/] per abilitare/disabilitare i singoli step (execute=true|false)");
            ConsolePlus.Write("4. Configura la sezione [Cyan]3.services[/]");
            ConsolePlus.Write("   - Mappa i nomi delle utenze di servizio con le relative password");
            ConsolePlus.Write("   - Esempio: [Cyan]\"TITESI\\\\svc_EINT_Batch_NAS\": \"PASSWORD\"[/]");
            ConsolePlus.Write("5. Configura la sezione [Cyan]1.robocopy[/] con source, destination ed exclude");
            ConsolePlus.Write("6. Configura la sezione [Cyan]1.requiredFolders[/] con le cartelle da garantire a fine fase 1");
            ConsolePlus.Write("7. Configura la sezione [Cyan]2.frameworkPaths[/] con i percorsi dei framework della fase 2");
            ConsolePlus.Write("8. Configura la sezione [Cyan]2.dllRegistrationPaths[/] con i percorsi DLL della fase 2");
            ConsolePlus.Write("9. Configura la sezione [Cyan]2.softwareToInstall[/] con installer prerequisiti (VC++, Java, Office)");
            ConsolePlus.Write("10. Ricorda che [Cyan]--target-ip[/] è obbligatorio per le fasi 4 e 5");
            Console.WriteLine();
            
            ConsolePlus.Write("[Yellow]Utilizzo:[/]");
            ConsolePlus.Write("Dopo aver completato la configurazione, esegui il migrator con:");
            ConsolePlus.Write("[Cyan]edi migrator -s SERVER_SORGENTE -c COMPUTER_SORGENTE -t TARGET_IP[/]");
            Console.WriteLine();
            
            ConsolePlus.WriteHr(70);
            
            return 0;
        }
        catch (Exception ex)
        {
            ConsolePlus.Write($"[Red]✗ Errore durante la creazione del file di configurazione: {ex.Message}[/]");
            return 1;
        }
    }

    private static void PrintBanner()
    {
        ConsolePlus.Write(@"[Cyan]
 __  __   ___    ____   ____       _      _____    ___    ____  
|  \/  | |_ _|  / ___| |  _ \     / \    |_   _|  / _ \  |  _ \ 
| |\/| |  | |  | |  _  | |_) |   / _ \     | |   | | | | | |_) |
| |  | |  | |  | |_| | |  _ <   / ___ \    | |   | |_| | |  _ < 
|_|  |_| |___|  \____| |_| \_\ /_/   \_\   |_|    \___/  |_| \_\
[/]                                                                 
[DarkGray]Script di migrazione dei server batch, clona le configurazioni
di un altro server definito in input[/]
");
        ConsolePlus.WriteHr(70);
        Console.WriteLine();
    }
}
