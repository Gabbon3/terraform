using terraform.lib.logger;
using terraform.lib.config;

namespace terraform.Migrator;

// =====================================
//  _____                         ____  
// |  ___|   __ _   ___    ___   | ___| 
// | |_     / _` | / __|  / _ \  |___ \ 
// |  _|   | (_| | \__ \ |  __/   ___) |
// |_|      \__,_| |___/  \___|  |____/ 
//                                       
// =====================================
// FASE 5: Cutover

/// <summary>
/// Fase 5 della migrazione: operazioni di cutover.
/// </summary>
/// <remarks>
/// Questa fase aggiorna il file hosts locale in modo idempotente, usando le entry
/// configurate in <c>5.hosts</c> del file di configurazione.
/// 
/// Placeholder supportati nella proprietà <c>host</c>:
/// - <c>{SourceServer}</c>: risolto al valore runtime di <see cref="MigratorConfig.SourceServer"/>.
/// 
/// Esempio di configurazione:
/// <code>
/// "5": {
///   "hosts": [
///     { "ip": "127.0.0.1", "host": "{SourceServer}" },
///     { "ip": "10.20.51.44", "host": "dashboard.e-integrationservice.net" }
///   ]
/// }
/// </code>
/// </remarks>
public sealed class Phase5
{
    private readonly MigratorConfig _config;
    private readonly Logger _logger;

    /// <summary>
    /// Inizializza la fase 5 della migrazione.
    /// </summary>
    /// <param name="config">Configurazione runtime del migrator.</param>
    /// <param name="logger">Logger centralizzato dell'esecuzione.</param>
    public Phase5(MigratorConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    #region Edit Hosts

    // ------------------------------------------------------------------------------
    // 5. AGGIORNAMENTO FILE HOSTS
    // ------------------------------------------------------------------------------
    /// <summary>
    /// Allinea il file hosts alle entry definite in configurazione (<c>5.hosts</c>).
    /// </summary>
    /// <remarks>
    /// Regole applicate per ogni mapping configurato:
    /// - Se l'host non esiste nel file, viene aggiunto.
    /// - Se l'host esiste con IP differente, viene aggiornato al nuovo IP.
    /// - Se esiste già con IP corretto, non viene modificato.
    /// - Se ci sono duplicati dello stesso host, vengono consolidati.
    /// 
    /// In modalità DryRun non vengono scritte modifiche su disco: vengono solo loggate
    /// le operazioni previste (ADD/UPDATE).
    /// </remarks>
    /// <returns>Task asincrono di completamento della fase.</returns>
    public async Task EditHostAsync()
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        var lines = (await File.ReadAllLinesAsync(hostsPath)).ToList();

        var configuredEntries = ConfigManager.Global.Phase5.Hosts;
        if (configuredEntries.Count == 0)
        {
            _logger.Write("Nessuna entry hosts configurata in sezione 5.hosts. Nessuna modifica apportata.", LogLevel.Warn);
            return;
        }

        var normalizedEntries = configuredEntries
            .Select(entry => new
            {
                Ip = entry.Ip.Trim(),
                Host = ResolveHostToken(entry.Host)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Ip) && !string.IsNullOrWhiteSpace(entry.Host))
            .ToList();

        if (normalizedEntries.Count == 0)
        {
            _logger.Write("Le entry della sezione 5.hosts non sono valide. Nessuna modifica apportata.", LogLevel.Warn);
            return;
        }

        var pendingChanges = new List<string>();
        foreach (var entry in normalizedEntries)
        {
            var result = EnsureHostMapping(lines, entry.Ip, entry.Host);

            if (result == HostMappingResult.Added)
            {
                pendingChanges.Add($"ADD {entry.Ip}\t{entry.Host}");
            }
            else if (result == HostMappingResult.Updated)
            {
                pendingChanges.Add($"UPDATE {entry.Ip}\t{entry.Host}");
            }
        }

        if (pendingChanges.Count == 0)
        {
            _logger.Write("Il file hosts è già allineato alle entry configurate in 5.hosts.", LogLevel.Info);
            return;
        }

        if (_config.DryRun)
        {
            foreach (var change in pendingChanges)
            {
                _logger.Write($"[DRYRUN] {change}", LogLevel.Info);
            }

            return;
        }

        await File.WriteAllLinesAsync(hostsPath, lines);

        foreach (var change in pendingChanges)
        {
            _logger.Write($"Hosts {change}", LogLevel.Success);
        }

        _logger.Write("File hosts aggiornato con successo.", LogLevel.Success);
    }

    /// <summary>
    /// Risolve eventuali placeholder nel nome host proveniente da configurazione.
    /// </summary>
    /// <param name="configuredHost">Valore host configurato in <c>5.hosts</c>.</param>
    /// <returns>
    /// Host risolto pronto per l'applicazione nel file hosts.
    /// Attualmente è supportato il placeholder <c>{SourceServer}</c>.
    /// </returns>
    /// <remarks>
    /// Placeholder attuale:
    /// - <c>{SourceServer}</c> - sostituito con <see cref="MigratorConfig.SourceServer"/>.
    /// 
    /// Se il valore non è un placeholder noto, viene restituito il testo originale
    /// ripulito dagli spazi laterali.
    /// </remarks>
    private string ResolveHostToken(string configuredHost)
    {
        if (string.Equals(configuredHost?.Trim(), "{SourceServer}", StringComparison.OrdinalIgnoreCase))
        {
            return _config.SourceServer;
        }

        return configuredHost?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Garantisce la presenza della mappatura host desiderata nell'elenco delle righe hosts.
    /// </summary>
    /// <param name="lines">Righe correnti del file hosts (modificate in-place).</param>
    /// <param name="expectedIp">IP che l'host deve avere dopo l'allineamento.</param>
    /// <param name="expectedHost">Hostname da cercare/aggiornare.</param>
    /// <returns>
    /// Stato dell'operazione:
    /// <see cref="HostMappingResult.Added"/> se è stata aggiunta una nuova riga,
    /// <see cref="HostMappingResult.Updated"/> se una riga esistente è stata aggiornata,
    /// <see cref="HostMappingResult.None"/> se il mapping era già corretto.
    /// </returns>
    /// <remarks>
    /// La ricerca è case-insensitive sul nome host. In caso di duplicati, mantiene una sola
    /// occorrenza coerente con l'IP atteso.
    /// </remarks>
    private static HostMappingResult EnsureHostMapping(List<string> lines, string expectedIp, string expectedHost)
    {
        var hostIndices = new List<int>();
        var exactIndices = new List<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryParseHostLine(lines[i], out var ip, out var host))
            {
                continue;
            }

            if (!string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hostIndices.Add(i);

            if (string.Equals(ip, expectedIp, StringComparison.OrdinalIgnoreCase))
            {
                exactIndices.Add(i);
            }
        }

        if (hostIndices.Count == 0)
        {
            lines.Add($"{expectedIp}\t{expectedHost}");
            return HostMappingResult.Added;
        }

        if (exactIndices.Count > 0)
        {
            var keep = exactIndices[0];
            for (var i = hostIndices.Count - 1; i >= 0; i--)
            {
                var index = hostIndices[i];
                if (index != keep)
                {
                    lines.RemoveAt(index);
                }
            }

            return HostMappingResult.None;
        }

        var first = hostIndices[0];
        lines[first] = $"{expectedIp}\t{expectedHost}";

        for (var i = hostIndices.Count - 1; i >= 1; i--)
        {
            lines.RemoveAt(hostIndices[i]);
        }

        return HostMappingResult.Updated;
    }

    /// <summary>
    /// Effettua il parsing di una riga del file hosts estraendo IP e primo hostname.
    /// </summary>
    /// <param name="line">Riga raw del file hosts.</param>
    /// <param name="ip">IP estratto, se il parsing va a buon fine.</param>
    /// <param name="host">Primo hostname estratto, se il parsing va a buon fine.</param>
    /// <returns>
    /// <c>true</c> se la riga contiene almeno IP + hostname;
    /// <c>false</c> per righe vuote, commenti o formato non valido.
    /// </returns>
    /// <remarks>
    /// Le righe commentate (prefisso <c>#</c>) sono ignorate.
    /// In presenza di alias multipli sulla stessa riga, viene considerato il primo hostname.
    /// </remarks>
    private static bool TryParseHostLine(string line, out string ip, out string host)
    {
        ip = string.Empty;
        host = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
        {
            return false;
        }

        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        ip = parts[0];
        host = parts[1];
        return true;
    }

    /// <summary>
    /// Esito dell'allineamento di una singola entry hosts.
    /// </summary>
    private enum HostMappingResult
    {
        /// <summary>Nessuna modifica necessaria: mapping già corretto.</summary>
        None,
        /// <summary>Mapping assente: riga aggiunta.</summary>
        Added,
        /// <summary>Mapping presente con IP differente: riga aggiornata.</summary>
        Updated
    }

    #endregion
}
