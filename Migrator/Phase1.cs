using terraform.lib.logger;
using terraform.lib.utils;
using terraform.lib.config;

namespace terraform.Migrator;

// =================================
//  _____                         _ 
// |  ___|   __ _   ___    ___   / |
// | |_     / _` | / __|  / _ \  | |
// |  _|   | (_| | \__ \ |  __/  | |
// |_|      \__,_| |___/  \___|  |_|
//                                  
// =================================
// FASE 1: Copia struttura base, pulizia e predisposizione cartelle

public sealed class Phase1
{
    private readonly MigratorConfig _config;
    private readonly Logger _logger;

    public Phase1(MigratorConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    #region Robocopy

    // ------------------------------------------------------------------------------
    // 1. COPIA DELLA STRUTTURA BASE CON ROBOCOPY
    // ------------------------------------------------------------------------------
    public async Task RobocopyAsync()
    {
        _logger.Write("Avvio copia strutture richieste con Robocopy. Attendere...", LogLevel.Info);

        var robocopyEntries = ConfigManager.Global.Phase1.Robocopy;
        if (robocopyEntries.Count == 0)
        {
            throw new InvalidOperationException("Nessuna configurazione robocopy trovata nel file migrator_config.json.");
        }

        foreach (var robocopyEntry in robocopyEntries)
        {
            if (string.IsNullOrWhiteSpace(robocopyEntry.Source) || string.IsNullOrWhiteSpace(robocopyEntry.Destination))
            {
                throw new InvalidOperationException("Ogni entry della sezione robocopy deve avere source e destination valorizzati.");
            }

            var sourcePath = ResolveSourcePath(robocopyEntry.Source);
            var destinationPath = robocopyEntry.Destination;

            var sourceIsDirectory = Directory.Exists(sourcePath);
            var sourceIsFile = File.Exists(sourcePath);

            if (!sourceIsDirectory && !sourceIsFile)
            {
                throw new FileNotFoundException($"Percorso sorgente non raggiungibile: {sourcePath}");
            }

            var args = BuildRobocopyArgs(sourcePath, destinationPath, sourceIsFile, robocopyEntry.Exclude);

            if (_config.DryRun)
            {
                _logger.Write($"[DRYRUN] robocopy {args}", LogLevel.Info);
                continue;
            }

            _logger.Write($"robocopy {args}", LogLevel.Info);

            var exitCode = await ProcessUtils.RunProcessAsync("robocopy", args);

            // Robocopy usa exit codes particolari (sotto l'8 è considerato successo)
            if (exitCode >= 0)
            {
                if (exitCode < 8)
                {
                    _logger.Write($"Copia completata con successo: {sourcePath}", LogLevel.Success);
                }
                else
                {
                    _logger.Write($"Errore durante la copia tramite Robocopy. Sorgente: {sourcePath}. Exit Code: {exitCode}", LogLevel.Error);
                }
            }
            else
            {
                _logger.Write($"Impossibile avviare il processo robocopy per: {sourcePath}", LogLevel.Error);
            }
        }

        // Pulisco cartelle di backup e preparo la cartella logs vuota
        CleanAndCreateExludedFolders();
        // inizializzo le cartelle richieste
        CreateRequiredFolders();

        _logger.Write("=== FASE 1 COMPLETATA: STRUTTURA BASE PRONTA ===", LogLevel.Success);
    }

    private string ResolveSourcePath(string configuredSource)
    {
        if (configuredSource.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return configuredSource;
        }

        var normalizedSource = configuredSource.TrimStart('\\', '/');
        return $@"\\{_config.SourceServer}\{normalizedSource}";
    }

    /// <summary>
    /// Costruisce gli argomenti per Robocopy mantenendo la replica completa dei metadati.
    /// </summary>
    /// <param name="sourcePath">Percorso sorgente completo (file o directory).</param>
    /// <param name="destinationPath">Percorso destinazione completo (file o directory).</param>
    /// <param name="sourceIsFile">Indica se la sorgente rappresenta un file singolo.</param>
    /// <param name="excludeFolders">Elenco cartelle da escludere (usato solo per copie directory).</param>
    /// <returns>Stringa argomenti pronta per il comando <c>robocopy</c>.</returns>
    /// <remarks>
    /// Switch principali utilizzati:
    /// - <c>/COPYALL</c>: copia dati, attributi, timestamp, ACL, owner e audit.
    /// - <c>/B</c>: usa backup mode per leggere/scrivere anche file con permessi restrittivi (se privilegi presenti).
    /// - <c>/DCOPY:DAT</c>: copia metadati delle directory (data, attributi e timestamp).
    /// - <c>/MIR</c>: mirror della struttura directory (solo quando la sorgente è cartella).
    /// - <c>/MT:16</c>: abilita copia multithread con 16 thread (solo quando la sorgente è cartella).
    /// - <c>/R:1</c> e <c>/W:1</c>: riduce retry e tempo di attesa per tentativo.
    /// - <c>/NP</c>: sopprime la progress percentage per log più compatti.
    /// - <c>/XD</c>: esclude directory specifiche configurate in input.
    /// - <c>/SECFIX</c>: riapplica la sicurezza anche sui file già presenti, solo se la directory di destinazione esiste già.
    /// </remarks>
    private static string BuildRobocopyArgs(string sourcePath, string destinationPath, bool sourceIsFile, IReadOnlyCollection<string> excludeFolders)
    {
        if (sourceIsFile)
        {
            var sourceDir = Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidOperationException($"Impossibile determinare la cartella sorgente del file: {sourcePath}");

            var destinationDir = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException($"Impossibile determinare la cartella destinazione del file: {destinationPath}");

            var fileName = Path.GetFileName(sourcePath);
            var secFixArg = Directory.Exists(destinationDir) ? " /SECFIX" : string.Empty;
            return $"\"{sourceDir}\" \"{destinationDir}\" \"{fileName}\" /COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP{secFixArg}";
        }

        var excludeArg = excludeFolders.Count > 0
            ? $" /XD {string.Join(' ', excludeFolders)}"
            : string.Empty;
        var secFixDirectoryArg = Directory.Exists(destinationPath) ? " /SECFIX" : string.Empty;

        return $"\"{sourcePath}\" \"{destinationPath}\" /MIR /MT:16 /COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP{secFixDirectoryArg}{excludeArg}";
    }

    // ------------------------------------------------------------------------------
    // 2. PULIZIA E CREAZIONE CARTELLE ESCLUSE DA ROBOCOPY
    // ------------------------------------------------------------------------------
    private void CleanAndCreateExludedFolders()
    {
        var targetDirs = GetExcludedTargetDirectories();

        foreach (var targetDir in targetDirs)
        {
            if (_config.DryRun)
            {
                _logger.Write($"[DRYRUN] Pulizia/Creazione: {targetDir}", LogLevel.Info);
                continue;
            }

            if (Directory.Exists(targetDir))
            {
                _logger.Write($"Svuotamento della cartella {targetDir} in corso...", LogLevel.Info);
                Directory.Delete(targetDir, true);
                Directory.CreateDirectory(targetDir);
                _logger.Write($"Cartella {targetDir} svuotata.", LogLevel.Success);
            }
            else
            {
                Directory.CreateDirectory(targetDir);
                _logger.Write($"Cartella {targetDir} creata vuota.", LogLevel.Info);
            }
        }
    }

    private List<string> GetExcludedTargetDirectories()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var robocopyEntry in ConfigManager.Global.Phase1.Robocopy)
        {
            if (robocopyEntry.Exclude.Count == 0 || string.IsNullOrWhiteSpace(robocopyEntry.Destination))
            {
                continue;
            }

            var destinationBase = Path.HasExtension(robocopyEntry.Destination)
                ? Path.GetDirectoryName(robocopyEntry.Destination)
                : robocopyEntry.Destination;

            if (string.IsNullOrWhiteSpace(destinationBase))
            {
                continue;
            }

            foreach (var excludedPath in robocopyEntry.Exclude)
            {
                if (string.IsNullOrWhiteSpace(excludedPath))
                {
                    continue;
                }

                var normalizedExcludedPath = excludedPath.Trim();
                var targetDir = Path.IsPathRooted(normalizedExcludedPath)
                    ? normalizedExcludedPath
                    : Path.Combine(destinationBase, normalizedExcludedPath);

                results.Add(targetDir);
            }
        }

        return [.. results];
    }

    #endregion
    #region Required Folders

    // ------------------------------------------------------------------------------
    // 3. CREAZIONE CARTELLE RICHIESTE
    // ------------------------------------------------------------------------------
    /// <summary>
    /// Crea le cartelle richieste e, se configurato, replica la struttura delle sottocartelle vuote dal server sorgente.
    /// </summary>
    private void CreateRequiredFolders()
    {
        foreach (var requiredFolder in ConfigManager.Global.Phase1.RequiredFolders)
        {
            if (string.IsNullOrWhiteSpace(requiredFolder.Path))
            {
                continue;
            }

            if (requiredFolder.RecursiveDepth < -1)
            {
                _logger.Write($"Profondità non valida ({requiredFolder.RecursiveDepth}) per {requiredFolder.Path}. Usare -1, 0, 1...", LogLevel.Error);
                continue;
            }

            if (_config.DryRun)
            {
                _logger.Write($"[DRYRUN] Creazione cartella richiesta: {requiredFolder.Path} (recursiveDepth={requiredFolder.RecursiveDepth})", LogLevel.Info);

                if (requiredFolder.RecursiveDepth != 0)
                {
                    var sourcePath = ResolveRequiredFolderSourcePath(requiredFolder.Path);
                    _logger.Write($"[DRYRUN] Replica struttura sottocartelle da {sourcePath} a {requiredFolder.Path}", LogLevel.Info);
                }

                continue;
            }

            if (!Directory.Exists(requiredFolder.Path))
            {
                Directory.CreateDirectory(requiredFolder.Path);
                _logger.Write($"Creata struttura directory: {requiredFolder.Path}", LogLevel.Success);
            }
            else
            {
                _logger.Write($"Directory {requiredFolder.Path} già esistente.", LogLevel.Warn);
            }

            if (requiredFolder.RecursiveDepth == 0)
            {
                continue;
            }

            var sourceRoot = ResolveRequiredFolderSourcePath(requiredFolder.Path);
            if (!Directory.Exists(sourceRoot))
            {
                _logger.Write($"Sorgente non raggiungibile per replica struttura: {sourceRoot}", LogLevel.Warn);
                continue;
            }

            ReplicateSubfolders(sourceRoot, requiredFolder.Path, requiredFolder.RecursiveDepth, 1);
        }
    }

    /// <summary>
    /// Converte un percorso locale in percorso UNC sul server sorgente usando la share amministrativa del drive.
    /// </summary>
    /// <param name="targetPath">Percorso locale target (es. D:\\Cartella\\Path).</param>
    /// <returns>Percorso UNC equivalente sul server sorgente.</returns>
    private string ResolveRequiredFolderSourcePath(string targetPath)
    {
        if (targetPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return targetPath;
        }

        var fullTargetPath = Path.GetFullPath(targetPath);

        if (fullTargetPath.Length < 3 || fullTargetPath[1] != ':')
        {
            return fullTargetPath;
        }

        var driveLetter = char.ToUpperInvariant(fullTargetPath[0]);
        var relativePath = fullTargetPath[3..].TrimStart('\\', '/');

        return string.IsNullOrWhiteSpace(relativePath)
            ? $@"\\{_config.SourceServer}\{driveLetter}$"
            : $@"\\{_config.SourceServer}\{driveLetter}$\{relativePath}";
    }

    /// <summary>
    /// Replica ricorsivamente solo la struttura delle sottocartelle (vuote) dal percorso sorgente al target.
    /// </summary>
    /// <param name="sourcePath">Percorso sorgente da cui leggere le sottocartelle.</param>
    /// <param name="targetPath">Percorso target dove creare la struttura.</param>
    /// <param name="maxDepth">Profondità massima: -1 tutte, 1 primo livello, 2 due livelli, ecc.</param>
    /// <param name="currentDepth">Profondità corrente della ricorsione.</param>
    private void ReplicateSubfolders(string sourcePath, string targetPath, int maxDepth, int currentDepth)
    {
        if (maxDepth >= 0 && currentDepth > maxDepth)
        {
            return;
        }

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(sourcePath);
        }
        catch (Exception ex)
        {
            _logger.Write($"Errore durante la lettura delle sottocartelle in {sourcePath}: {ex.Message}", LogLevel.Error);
            return;
        }

        foreach (var sourceSubdirectory in subdirectories)
        {
            var folderName = Path.GetFileName(sourceSubdirectory);
            var targetSubdirectory = Path.Combine(targetPath, folderName);

            try
            {
                if (!Directory.Exists(targetSubdirectory))
                {
                    Directory.CreateDirectory(targetSubdirectory);
                    _logger.Write($"Creata sottocartella: {targetSubdirectory}", LogLevel.Success);
                }

                ReplicateSubfolders(sourceSubdirectory, targetSubdirectory, maxDepth, currentDepth + 1);
            }
            catch (Exception ex)
            {
                _logger.Write($"Errore durante la creazione di {targetSubdirectory}: {ex.Message}", LogLevel.Error);
            }
        }
    }

    #endregion
}
