using System.Security.Principal;
using terraform.lib.config;
using terraform.lib.console;
using terraform.lib.logger;

namespace terraform.Migrator;

// ==============================================================================
//  __  __      _      ___   _   _ 
// |  \/  |    / \    |_ _| | \ | |
// | |\/| |   / _ \    | |  |  \| |
// | |  | |  / ___ \   | |  | |\  |
// |_|  |_| /_/   \_\ |___| |_| \_|
//
// Orchestratore principale del Migrator
// ==============================================================================

public sealed class MigratorOrchestrator
{
    private readonly MigratorConfig _config;
    private readonly Logger _logger;
    private readonly ConfigManager _configManager;
    private readonly Phase1 _phase1;
    private readonly Phase2 _phase2;
    private readonly Phase3 _phase3;
    private readonly Phase4 _phase4;
    private readonly Phase5 _phase5;

    /// <summary>
    /// Inizializza l'orchestratore principale delle fasi di migrazione.
    /// </summary>
    /// <param name="config">Configurazione runtime del comando migrator.</param>
    public MigratorOrchestrator(MigratorConfig config)
    {
        _config = config;
        _logger = new Logger(config.LogDirectory, $"MIGRAZIONE_SERVER_{DateTime.Now:yyyy-MM-dd_HH-mm}");
        _configManager = ConfigManager.Instance;
        _phase1 = new Phase1(config, _logger);
        _phase2 = new Phase2(config, _logger);
        _phase3 = new Phase3(config, _logger);
        _phase4 = new Phase4(config, _logger);
        _phase5 = new Phase5(config, _logger);
    }

    /// <summary>
    /// Esegue il processo di migrazione completo
    /// </summary>
    public async Task<bool> RunAsync()
    {
        try
        {
            // Step preliminare di controllo dell'ambiente (Privilegi e Versione)
            if (!PreFlightCheck())
            {
                _logger.Write("Pre-flight check fallito. Interruzione script.", LogLevel.Error);
                return false;
            }

            // Caricamento configurazione globale
            _logger.Write("Caricamento configurazione da file...", LogLevel.Info);
            var configLoaded = await _configManager.LoadAsync();
            
            if (!configLoaded)
            {
                _logger.Write("ATTENZIONE: File di configurazione non trovato o non valido.", LogLevel.Warn);
                _logger.Write("E' stato creato un file template 'migrator_config.json' nella cartella dell'eseguibile.", LogLevel.Warn);
                _logger.Write("Compilare il file con config.database, steps, 1.robocopy, 1.requiredFolders, 2.frameworkPaths, 2.dllRegistrationPaths, 2.softwareToInstall, 3.services, 4 e 5 prima di procedere.", LogLevel.Warn);
            }
            else
            {
                _logger.Write($"Caricata configurazione con {_configManager.ServiceCount} credenziali servizi.", LogLevel.Success);
            }

            _logger.Write($"DryRun: {_config.DryRun}", LogLevel.Info);
            if (_config.StartFrom != null)
            {
                _logger.Write($"Parametro StartFrom: {_config.StartFrom}", LogLevel.Info);
            }
            if (_config.RunOnly != null)
            {
                _logger.Write($"Parametro RunOnly: {_config.RunOnly}", LogLevel.Info);
            }

            if (_config.StartFrom != null && _config.RunOnly != null)
            {
                _logger.Write("Specificati sia -StartFrom che -RunOnly. Verrà data priorità a -RunOnly.", LogLevel.Warn);
            }

            // Esecuzione degli step
            var steps = MigratorConfig.AvailableSteps;
            var skipToIndex = _config.StartFrom != null 
                ? Array.IndexOf(steps, _config.StartFrom) 
                : 0;

            for (int i = 0; i < steps.Length; i++)
            {
                var step = steps[i];

                // --- LOGICA DI SKIP (RunOnly vs StartFrom) ---
                if (_config.RunOnly != null)
                {
                    if (step != _config.RunOnly)
                    {
                        _logger.Write($"SKIP STEP (RunOnly): {step}", LogLevel.Info);
                        continue;
                    }
                }
                else if (_config.StartFrom != null)
                {
                    if (i < skipToIndex)
                    {
                        _logger.Write($"SKIP STEP (StartFrom): {step}", LogLevel.Info);
                        continue;
                    }
                }

                if (!_configManager.Current.Steps.ShouldExecute(step))
                {
                    ConsolePlus.Write($"\n[Yellow]#[DarkGray] ------- [Yellow]SKIP {step}[DarkGray] ------- [Yellow]#[/]\n");
                    _logger.Write($"SKIP STEP (Config execute=false): {step}", LogLevel.Info);
                    continue;
                }

                // --- VERIFICA CREDENZIALI SERVIZI PER STEP CHE LE RICHIEDONO ---
                if ((step == "CreateServices" || step == "CreateTasks") && _configManager.ServiceCount == 0)
                {
                    _logger.Write($"SKIP STEP {step}: Nessuna credenziale caricata.", LogLevel.Warn);
                    continue;
                }

                // --- ESECUZIONE DINAMICA DELLO STEP ---
                ConsolePlus.Write($"\n[Green]#[DarkGray] ------- [Green]{step}[DarkGray] ------- [Green]#[/]\n");
                _logger.Write($"=== ESECUZIONE STEP: {step} ===", LogLevel.Info);

                try
                {
                    await ExecuteStepAsync(step);
                }
                catch (Exception ex)
                {
                    _logger.Write($"STEP {step} terminato con un'eccezione critica: {ex.Message}", LogLevel.Error);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Write($"Errore fatale: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    // ==============================================================================
    // Step preliminare di controllo dell'ambiente (Privilegi)
    // ==============================================================================
    /// <summary>
    /// Esegue i controlli preliminari di ambiente prima della migrazione.
    /// </summary>
    /// <returns><c>true</c> se i controlli sono superati; altrimenti <c>false</c>.</returns>
    private bool PreFlightCheck()
    {
        _logger.Write("Esecuzione controlli preliminari...", LogLevel.Info);

        // Controllo Privilegi di Amministratore
        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        if (!isAdmin)
        {
            _logger.Write("ATTENZIONE: Lo script NON è stato avviato come Amministratore.", LogLevel.Error);
            _logger.Write("Le registrazioni DLL (regasm/regsvr32) e i comandi di servizio (sc.exe) falliranno.", LogLevel.Error);
            return false;
        }

        _logger.Write("Privilegi di Amministratore confermati.", LogLevel.Success);
        return true;
    }

    // Esecuzione dinamica degli step
    /// <summary>
    /// Esegue dinamicamente uno step della pipeline di migrazione.
    /// </summary>
    /// <param name="step">Nome logico dello step da eseguire.</param>
    /// <returns>Task asincrono di completamento dello step.</returns>
    private async Task ExecuteStepAsync(string step)
    {
        switch (step)
        {
            case "Robocopy":
                await _phase1.RobocopyAsync();
                break;
            case "SoftwareToInstall":
                await _phase2.InstallSoftwaresAsync();
                break;
            case "RegisterDll":
                await _phase2.RegisterDllAsync();
                break;
            case "InstallMsi":
                await _phase2.InstallEdiFrameworkAsync();
                break;
            case "CreateServices":
                await _phase3.CreateServicesAsync();
                break;
            case "CreateTasks":
                await _phase3.CreateTasksAsync();
                break;
            case "UpdateCleaningConfig":
                await _phase4.UpdateCleaningConfigAsync();
                break;
            case "EditHost":
                await _phase5.EditHostAsync();
                break;
            default:
                throw new InvalidOperationException($"Step non riconosciuto: {step}");
        }
    }
}
