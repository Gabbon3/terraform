using System.Text.Json;
using System.Xml.Linq;
using terraform.lib.config;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.Migrator;

// =====================================
//  _____                         _____ 
// |  ___|   __ _   ___    ___   |___ / 
// | |_     / _` | / __|  / _ \    |_ \ 
// |  _|   | (_| | \__ \ |  __/   ___) |
// |_|      \__,_| |___/  \___|  |____/ 
//                                      
// =====================================
// FASE 3: Clonazione servizi e task schedulati (CON CREDENZIALI)

public sealed class Phase3
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MigratorConfig _config;
    private readonly Logger _logger;

    /// <summary>
    /// Inizializza la fase 3 della migrazione (servizi e task schedulati).
    /// </summary>
    /// <param name="config">Configurazione runtime del migrator.</param>
    /// <param name="logger">Logger centralizzato dell'esecuzione.</param>
    public Phase3(MigratorConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    #region Servizi

    // ----------------------------------------------------------------------------
    // 3A. MIGRAZIONE SERVIZI WINDOWS
    // ----------------------------------------------------------------------------
    /// <summary>
    /// Crea i servizi sul server target leggendo i servizi dal server sorgente.
    /// </summary>
    /// <returns>Task asincrono di completamento della fase.</returns>
    public async Task CreateServicesAsync()
    {
        _logger.Write($"Interrogazione dei Servizi su {_config.SourceServer}...", LogLevel.Info);

        try
        {
            // Query remota dei servizi usando PowerShell con output NDJSON
            var psScript = $@"
                Get-CimInstance Win32_Service -ComputerName {_config.SourceComputerName} | 
                Where-Object {{ $_.StartName -like '*svc_*' }} | 
                Select-Object Name, DisplayName, PathName, StartName, StartMode | 
                ForEach-Object {{ $_ | ConvertTo-Json -Depth 5 -Compress }}
            ";

            await foreach (var line in ProcessUtils.ExecuteAndStreamPowerShellAsync(psScript))
            {
                var service = JsonSerializer.Deserialize<ServiceDto>(line, JsonOptions);
                if (service == null)
                    continue;

                var name = service.Name?.Trim();
                var displayName = service.DisplayName?.Trim() ?? name ?? string.Empty;
                var pathName = service.PathName?.Trim() ?? string.Empty;
                var startName = service.StartName?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(name))
                    continue;

                _logger.Write($"Creazione Servizio: {name}", LogLevel.Info);

                // Pulisce le virgolette dal percorso
                var cleanPath = pathName.Replace("\"", string.Empty);

                // Verifica se il servizio esiste già
                if (await ServiceExistsAsync(name))
                {
                    _logger.Write($"Il servizio {name} esiste già.", LogLevel.Warn);
                    continue;
                }

                var exePath = ExtractExePath(pathName);
                if (!string.IsNullOrWhiteSpace(exePath) && !File.Exists(exePath))
                {
                    _logger.Write($"Servizio '{name}' orfano: l'eseguibile '{exePath}' non è presente sul server. Verrà creato comunque.", LogLevel.Warn);
                }

                if (_config.DryRun)
                {
                    _logger.Write($"[DRYRUN] Creo il servizio: {name}, {displayName} -- {cleanPath}", LogLevel.Info);
                }
                else
                {
                    // Creazione base con sc.exe create
                    var createArgs = $"create \"{name}\" binPath= \"\"{cleanPath}\"\" DisplayName= \"{displayName}\" start= disabled";
                    var createExitCode = await ProcessUtils.RunProcessAsync("sc.exe", createArgs);

                    if (createExitCode == 0)
                    {
                        // Applicazione credenziali se l'utente è mappato nel dizionario
                        if (ConfigManager.Instance.TryGetServicePassword(startName, out var password))
                        {
                            // Nota: sc config richiede lo spazio dopo il simbolo =
                            var configArgs = $"config \"{name}\" obj= \"{startName}\" password= \"{password}\"";
                            var configExitCode = await ProcessUtils.RunProcessAsync("sc.exe", configArgs);

                            if (configExitCode == 0)
                            {
                                _logger.Write($"Servizio {name} creato (DISABLED) con utenza {startName}", LogLevel.Success);
                            }
                            else
                            {
                                _logger.Write($"Servizio {name} creato, ma fallita assegnazione credenziali. ExitCode: {configExitCode}", LogLevel.Error);
                            }
                        }
                        else
                        {
                            _logger.Write($"Servizio {name} creato (DISABLED). ATTENZIONE: Utenza {startName} non trovata nel file credenziali.", LogLevel.Warn);
                        }
                    }
                    else
                    {
                        _logger.Write($"Fallita creazione servizio {name}. ExitCode: {createExitCode}", LogLevel.Error);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Write($"Errore durante l'interrogazione dei servizi: {ex.Message}", LogLevel.Error);
        }
    }

    #endregion
    #region Task Sched.

    // ----------------------------------------------------------------------------
    // 3B. MIGRAZIONE TASK SCHEDULATI
    // ----------------------------------------------------------------------------
    /// <summary>
    /// Crea i task schedulati sul server target leggendo i task dal server sorgente.
    /// </summary>
    /// <returns>Task asincrono di completamento della fase.</returns>
    public async Task CreateTasksAsync()
    {
        _logger.Write($"Interrogazione dei Task Schedulati su {_config.SourceServer}...", LogLevel.Info);

        try
        {
            // Query remota dei task usando PowerShell con output NDJSON
            var psScript = $@"
                $ErrorActionPreference = 'SilentlyContinue'
                Invoke-Command -ComputerName {_config.SourceComputerName} -ScriptBlock {{
                    $ErrorActionPreference = 'SilentlyContinue'
                    Get-ScheduledTask | Where-Object {{ $_.Principal.UserId -like '*svc_*' }} | ForEach-Object {{
                        [PSCustomObject]@{{
                            TaskName  = $_.TaskName
                            TaskPath  = $_.TaskPath
                            Xml       = (Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -ErrorAction SilentlyContinue)
                            Principal = $_.Principal.UserId
                        }} | ConvertTo-Json -Depth 5 -Compress
                    }}
                }}
            ";

            await foreach (var line in ProcessUtils.ExecuteAndStreamPowerShellAsync(psScript))
            {
                var task = JsonSerializer.Deserialize<TaskDto>(line, JsonOptions);
                if (task == null)
                    continue;

                var taskName = task.TaskName?.Trim();
                var taskPath = task.TaskPath?.Trim() ?? "\\";
                var taskUser = task.Principal?.Trim() ?? string.Empty;
                var xmlContent = task.Xml ?? string.Empty;

                if (string.IsNullOrEmpty(taskName))
                    continue;

                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    _logger.Write($"Task {taskName} ignorato: XML vuoto o non disponibile.", LogLevel.Warn);
                    continue;
                }

                _logger.Write($"Migrazione Task: {taskName}", LogLevel.Info);

                if (_config.DryRun)
                {
                    _logger.Write($"[DRYRUN] Creazione Task {taskName} -- {taskPath}", LogLevel.Info);
                    continue;
                }

                // Salva XML temporaneamente
                var tempXmlPath = Path.Combine(Path.GetTempPath(), $"{taskName}.xml");
                
                // Disabilita il task a livello generale (Settings/Enabled), senza alterare i trigger.
                var modifiedXml = DisableTaskAtGeneralLevel(xmlContent);

                await File.WriteAllTextAsync(tempXmlPath, modifiedXml);

                // Registra il task con schtasks.exe
                if (ConfigManager.Instance.TryGetServicePassword(taskUser, out var password))
                {
                    var args = $"/Create /TN \"{taskPath}{taskName}\" /XML \"{tempXmlPath}\" /RU \"{taskUser}\" /RP \"{password}\" /F";
                    var exitCode = await ProcessUtils.RunProcessAsync("schtasks.exe", args);

                    if (exitCode == 0)
                    {
                        _logger.Write($"Task {taskName} registrato (DISABLED) sotto utenza {taskUser}.", LogLevel.Success);
                    }
                    else
                    {
                        _logger.Write($"Errore durante la registrazione del Task {taskName}. ExitCode: {exitCode}", LogLevel.Error);
                    }
                }
                else
                {
                    _logger.Write($"Task {taskName} ignorato. Utente {taskUser} non trovato nel file credenziali.", LogLevel.Warn);
                }

                // Cleanup file temporaneo
                if (File.Exists(tempXmlPath))
                {
                    File.Delete(tempXmlPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Write($"Errore durante l'interrogazione dei task: {ex.Message}", LogLevel.Error);
        }
    }

    #endregion
    #region Helper

    // ----------------------------------------------------------------------------
    // HELPER METHODS
    // -----------------------------------------------------------------------------

    // Verifica se un servizio esiste usando sc.exe query
    /// <summary>
    /// Verifica se un servizio Windows esiste già sul server target.
    /// </summary>
    /// <param name="serviceName">Nome del servizio da verificare.</param>
    /// <returns><c>true</c> se il servizio è presente; altrimenti <c>false</c>.</returns>
    private static async Task<bool> ServiceExistsAsync(string serviceName)
    {
        try
        {
            var exitCode = await ProcessUtils.RunProcessAsync("sc.exe", $"query \"{serviceName}\"", suppressOutput: true);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Estrae il percorso dell'eseguibile da una stringa PathName di servizio.
    /// </summary>
    /// <param name="rawPath">PathName grezzo che può includere virgolette e argomenti.</param>
    /// <returns>Percorso pulito del file eseguibile, oppure stringa vuota se non ricavabile.</returns>
    private static string ExtractExePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var trimmed = rawPath.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closingQuoteIndex = trimmed.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                return trimmed.Substring(1, closingQuoteIndex - 1).Trim();
            }
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return trimmed[..(exeIndex + 4)].Trim('"', ' ');
        }

        var firstToken = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstToken?.Trim('"') ?? string.Empty;
    }

    /// <summary>
    /// Imposta il task come disabilitato agendo solo su Settings/Enabled nell'XML.
    /// </summary>
    /// <param name="xmlContent">XML del task esportato.</param>
    /// <returns>XML aggiornato con Settings/Enabled=false; se non modificabile, XML originale.</returns>
    private static string DisableTaskAtGeneralLevel(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return xmlContent;
        }

        try
        {
            var document = XDocument.Parse(xmlContent, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null)
            {
                return xmlContent;
            }

            var ns = root.Name.Namespace;
            var settingsElement = root.Element(ns + "Settings");
            if (settingsElement == null)
            {
                return xmlContent;
            }

            var enabledElement = settingsElement.Element(ns + "Enabled");
            if (enabledElement == null)
            {
                settingsElement.AddFirst(new XElement(ns + "Enabled", "false"));
            }
            else
            {
                enabledElement.Value = "false";
            }

            return document.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return xmlContent;
        }
    }

    private sealed record ServiceDto
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? PathName { get; init; }
        public string? StartName { get; init; }
    }

    private sealed record TaskDto
    {
        public string? TaskName { get; init; }
        public string? TaskPath { get; init; }
        public string? Xml { get; init; }
        public string? Principal { get; init; }
    }

    #endregion
}
