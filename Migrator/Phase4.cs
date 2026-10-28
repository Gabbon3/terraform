using terraform.lib.config;
using terraform.lib.db;
using terraform.lib.logger;

namespace terraform.Migrator;

// =====================================
//  _____                         _  _   
// |  ___|   __ _   ___    ___   | || |  
// | |_     / _` | / __|  / _ \  | || |_ 
// |  _|   | (_| | \__ \ |  __/  |__   _|
// |_|      \__,_| |___/  \___|     |_|  
//                                        
// =====================================
// FASE 4: Configurazioni a DB

/// <summary>
/// Fase 4 della migrazione: aggiornamento configurazioni a database.
/// </summary>
public sealed class Phase4
{
    private readonly MigratorConfig _config;
    private readonly Logger _logger;

    /// <summary>
    /// Inizializza la fase 4 della migrazione.
    /// </summary>
    /// <param name="config">Configurazione runtime del migrator.</param>
    /// <param name="logger">Logger centralizzato dell'esecuzione.</param>
    public Phase4(MigratorConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    #region Update Cleaning Config

    // ------------------------------------------------------------------------------
    // 4. AGGIORNAMENTO CONFIGURAZIONE PULIZIE (IP SERVER)
    // ------------------------------------------------------------------------------
    /// <summary>
    /// Aggiorna l'IP del server per il logical name indicato nel database pulizie.
    /// </summary>
    /// <returns>Task asincrono di completamento della fase.</returns>
    public async Task UpdateCleaningConfigAsync()
    {
        const string sql = @"
UPDATE ip
SET ip.value = @NewIp
FROM cfg.t1216_eintegration_ip_address ip
INNER JOIN cfg.t1213_eintegration_server server ON ip.t1213_id = server.id
WHERE server.logical_name = @LogicalName;";

        var connectionString = ConfigManager.Global.Config.Database.Pulizie;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.Write("Connection string pulizie non configurata (config.database.pulizie).", LogLevel.Warn);
            return;
        }

        var logicalName = _config.SourceServer;
        var newIp = _config.TargetIp;

        if (_config.DryRun)
        {
            _logger.Write($"[DRYRUN] Update cfg.t1216_eintegration_ip_address -> logical_name={logicalName}, newIp={newIp}", LogLevel.Info);
            return;
        }

        var db = new DbManager(connectionString);
        var affectedRows = await db.ExecuteAsync(sql, new { NewIp = newIp, LogicalName = logicalName });

        if (affectedRows > 0)
        {
            _logger.Write($"Aggiornamento configurazione pulizie completato. Righe modificate: {affectedRows}", LogLevel.Success);
        }
        else
        {
            _logger.Write($"Nessuna riga trovata per logical_name '{logicalName}' nel database pulizie.", LogLevel.Warn);
        }
    }

    #endregion
}
