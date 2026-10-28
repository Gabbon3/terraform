using System.Collections.Concurrent;
using terraform.lib.config;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.Migrator;

// =====================================
//  _____                         ____  
// |  ___|   __ _   ___    ___   |___ \ 
// | |_     / _` | / __|  / _ \    __) |
// |  _|   | (_| | \__ \ |  __/   / __/ 
// |_|      \__,_| |___/  \___|  |_____|
//                                      
// =====================================
// FASE 2: Installazione framework e registrazione DLL

public sealed class Phase2
{
    private readonly MigratorConfig _config;
    private readonly Logger _logger;

    public Phase2(MigratorConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    #region Install MSI

    // ------------------------------------------------------------------------------
    // INSTALLAZIONE SOFTWARE PREREQUISITI (Visual C++, Java 7, Office 2000 + SP3)
    // ------------------------------------------------------------------------------
    /// <summary>
    /// Installa i prerequisiti software della fase 2 leggendo i percorsi dalla sezione
    /// <c>2.softwareToInstall</c> della configurazione globale.
    /// </summary>
    /// <remarks>
    /// Ogni installer automatizzato viene eseguito in modalità silente.
    /// L'installazione di Office 2000 SP3 è esclusa dall'automazione.
    /// </remarks>
    /// <remarks>
    /// ATTENZIONE: questo metodo è estremamente specifico per i software e le versioni indicate.
    /// Anche se sono censiti dal file di config, i percorsi sono obbligatoriamente quelli del template
    /// Qualsiasi altro software di quel genere dovrà essere seguito da un refactoring di questo metodo
    /// </remarks>
    public async Task InstallSoftwaresAsync()
    {
        var softwareConfig = ConfigManager.Global.Phase2.SoftwareToInstall;
        var javaInstallerPath = softwareConfig.Prerequisites.JavaInstallerPath;

        _logger.Write("Avvio installazione prerequisiti software (fase 2)...", LogLevel.Info);

        // VISUAL C++ 2015
        if (string.IsNullOrWhiteSpace(softwareConfig.VcppInstallerPath) || !File.Exists(softwareConfig.VcppInstallerPath))
        {
            _logger.Write($"Visual C++ 2015 non installato: file non trovato ({softwareConfig.VcppInstallerPath}).", LogLevel.Warn);
        }
        else if (_config.DryRun)
        {
            _logger.Write($"[DRYRUN] {softwareConfig.VcppInstallerPath} /install /quiet /norestart", LogLevel.Info);
        }
        else
        {
            var exitCode = await ProcessUtils.RunProcessAsync(softwareConfig.VcppInstallerPath, "/install /quiet /norestart");
            if (exitCode == 0 || exitCode == 3010 || exitCode == 1638)
            {
                _logger.Write("Installazione Visual C++ 2015 completata.", LogLevel.Success);
            }
            else
            {
                _logger.Write($"Installazione Visual C++ 2015 fallita. Exit Code: {exitCode}", LogLevel.Error);
            }
        }

        // JAVA 7
        if (string.IsNullOrWhiteSpace(javaInstallerPath) || !File.Exists(javaInstallerPath))
        {
            _logger.Write($"Java 7 non installato: file non trovato ({javaInstallerPath}).", LogLevel.Warn);
        }
        else if (_config.DryRun)
        {
            _logger.Write($"[DRYRUN] {javaInstallerPath} /s", LogLevel.Info);
        }
        else
        {
            var exitCode = await ProcessUtils.RunProcessAsync(javaInstallerPath, "/s");
            if (exitCode == 0 || exitCode == 3010 || exitCode == 1638)
            {
                _logger.Write("Installazione Java 7 completata.", LogLevel.Success);
            }
            else
            {
                _logger.Write($"Installazione Java 7 fallita. Exit Code: {exitCode}", LogLevel.Error);
            }
        }

        _logger.Write("ATTENZIONE: L'installazione di Office 2000 SP3 e' stata esclusa dall'automazione. Ricordarsi di installarlo MANUALMENTE montando la ISO.", LogLevel.Warn);

        _logger.Write("Installazione prerequisiti software completata.", LogLevel.Success);
    }

    // ------------------------------------------------------------------------------
    // INSTALLAZIONE LIBRERIE E FRAMEWORK (e-Integration & LightFramework)
    // ------------------------------------------------------------------------------
    /// <summary>
    /// Installa i framework MSI della fase 2.
    /// </summary>
    public async Task InstallEdiFrameworkAsync()
    {
        var frameworkPaths = ConfigManager.Global.Phase2.FrameworkPaths;
        if (frameworkPaths.Count == 0)
        {
            _logger.Write("Nessun percorso configurato in 2.frameworkPaths. Nessun framework da installare.", LogLevel.Warn);
            return;
        }

        foreach (var basePath in frameworkPaths)
        {
            _logger.Write($"Analisi e installazione framework da: {basePath}", LogLevel.Info);

            if (!Directory.Exists(basePath))
            {
                _logger.Write($"Impossibile raggiungere il percorso di rete: {basePath}. Installazione saltata.", LogLevel.Error);
                continue;
            }

            // Recupera le directory delle versioni, ordinandole alfanumericamente per sicurezza
            var versionFolders = Directory.GetDirectories(basePath)
                .OrderBy(d => d)
                .ToArray();

            foreach (var folder in versionFolders)
            {
                var folderName = Path.GetFileName(folder);
                _logger.Write($"-> Verifica versione {folderName}...", LogLevel.Info);

                // Cerca i file .msi, li ordina per data di ultima modifica decrescente e prende il primo (il pi� recente)
                var latestMsi = Directory.EnumerateFiles(folder, "*.msi", SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .FirstOrDefault();

                if (latestMsi == null)
                {
                    _logger.Write($"Nessun pacchetto MSI trovato in {folder}.", LogLevel.Warn);
                    continue;
                }

                var msiName = Path.GetFileName(latestMsi);
                _logger.Write($"Avvio installazione background di: {msiName}", LogLevel.Info);

                // Parametri msiexec: 
                // /i installa
                // /qn modalit� silente senza interfaccia
                // /norestart previene riavvii inattesi del server durante il provisioning
                var args = $"/i \"{latestMsi}\" /qn /norestart";

                if (_config.DryRun)
                {
                    _logger.Write($"[DRYRUN] msiexec.exe {args}", LogLevel.Info);
                }
                else
                {
                    var exitCode = await ProcessUtils.RunProcessAsync("msiexec.exe", args);

                    if (exitCode >= 0)
                    {
                        // I codici di ritorno standard di successo per msiexec sono 0 (Success) e 3010 (Reboot Required)
                        if (exitCode == 0 || exitCode == 3010)
                        {
                            _logger.Write($"Installazione di {msiName} completata.", LogLevel.Success);
                        }
                        else
                        {
                            _logger.Write($"Errore irreversibile su {msiName}. Exit Code: {exitCode}", LogLevel.Error);
                        }
                    }
                    else
                    {
                        _logger.Write($"Impossibile avviare msiexec.exe per {msiName}.", LogLevel.Error);
                    }
                }
            }
        }

    }

    #endregion
    #region Register DLL

    // ------------------------------------------------------------------------------
    // REGISTRAZIONE DELLE DLL (Parallela con Parallel.ForEachAsync)
    // ------------------------------------------------------------------------------
    public async Task RegisterDllAsync()
    {
        _logger.Write("Avvio registrazione parallela delle DLL (Solo log errori)...", LogLevel.Info);

        var registrationPaths = ConfigManager.Global.Phase2.DllRegistrationPaths;
        if (registrationPaths.Count == 0)
        {
            _logger.Write("Nessun percorso configurato in 2.dllRegistrationPaths. Nessuna DLL da registrare.", LogLevel.Warn);
            return;
        }

        var regAsmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"Microsoft.NET\Framework\v4.0.30319\regasm.exe");

        if (!File.Exists(regAsmPath))
        {
            throw new FileNotFoundException("File regasm.exe non trovato, necessario per componenti .NET.");
        }

        foreach (var registrationPath in registrationPaths)
        {
            if (string.IsNullOrWhiteSpace(registrationPath.Path))
            {
                continue;
            }

            _logger.Write($"Avvio registrazione DLL nel percorso: {registrationPath.Path} (recursive: {registrationPath.Recursive})", LogLevel.Info);

            if (!Directory.Exists(registrationPath.Path))
            {
                _logger.Write($"Percorso DLL non raggiungibile: {registrationPath.Path}", LogLevel.Warn);
                continue;
            }

            var dlls = Directory.EnumerateFiles(
                    registrationPath.Path,
                    "*.dll",
                    registrationPath.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .ToArray();
            
            if (dlls.Length == 0)
            {
                _logger.Write($"Nessuna DLL trovata nel percorso: {registrationPath.Path}", LogLevel.Warn);
                continue;
            }

            var errors = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(dlls, new ParallelOptions { MaxDegreeOfParallelism = 16 },
                async (dllPath, ct) =>
                {
                    var name = Path.GetFileName(dllPath);

                    if (_config.DryRun)
                    {
                        return;
                    }

                    try
                    {
                        if (name.StartsWith("ASBY", StringComparison.OrdinalIgnoreCase))
                        {
                            // Skip ASBY DLLs
                            return;
                        }

                        if (name.StartsWith("EURN", StringComparison.OrdinalIgnoreCase))
                        {
                            // Usa regasm per DLL .NET
                            var exitCode = await ProcessUtils.RunProcessAsync(regAsmPath, $"\"{dllPath}\" /codebase /silent");
                            if (exitCode != 0)
                            {
                                errors.Add($"Fallita regasm.exe su {name} (Exit: {exitCode})");
                            }
                        }
                        else if (name.StartsWith("EURC", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("EURW", StringComparison.OrdinalIgnoreCase))
                        {
                            // Usa regsvr32 per DLL COM
                            var exitCode = await ProcessUtils.RunProcessAsync("regsvr32.exe", $"/s \"{dllPath}\"");
                            if (exitCode != 0)
                            {
                                errors.Add($"Fallita regsvr32.exe su {name} (Exit: {exitCode})");
                            }
                        }
                        else
                        {
                            // Prova a determinare se è .NET o COM
                            var isDotNet = IsDotNetAssembly(dllPath);

                            if (isDotNet)
                            {
                                var exitCode = await ProcessUtils.RunProcessAsync(regAsmPath, $"\"{dllPath}\" /codebase /silent");
                                if (exitCode != 0)
                                {
                                    errors.Add($"Fallita regasm.exe su {name} (Exit: {exitCode})");
                                }
                            }
                            else
                            {
                                // Prova registrazione COM
                                var exitCode = await ProcessUtils.RunProcessAsync("regsvr32.exe", $"/s \"{dllPath}\"");
                                if (exitCode != 0)
                                {
                                    errors.Add($"Fallita regsvr32.exe su {name} (Exit: {exitCode})");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Eccezione durante registrazione di {name}: {ex.Message}");
                    }
                });

            if (errors.IsEmpty)
            {
                _logger.Write($"Registrazione DLL completata senza errori per il percorso: {registrationPath.Path}", LogLevel.Success);
                continue;
            }

            _logger.Write($"Errori rilevati durante la registrazione DLL nel percorso: {registrationPath.Path}", LogLevel.Error);
            foreach (var error in errors)
            {
                _logger.Write(error, LogLevel.Error, isParallel: true);
            }
        }

        _logger.Write("Registrazione DLL completata secondo matrice operativa.", LogLevel.Success);
        _logger.Write("=== FASE 2 COMPLETATA ===", LogLevel.Success);
    }

    #endregion
    #region Helper

    // Helper per verificare se una DLL è un assembly .NET
    private static bool IsDotNetAssembly(string path)
    {
        try
        {
            _ = System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            // Non è un assembly .NET
            return false;
        }
        catch
        {
            // Altri errori
            return false;
        }
    }

    #endregion
}
