using Dapper;
using Microsoft.Data.SqlClient;

namespace terraform.lib.db;

public sealed class DbManager
{
    private readonly string _connectionString;

    /// <summary>
    /// Inizializza un nuovo manager dati con la connection string SQL.
    /// </summary>
    /// <param name="connectionString">Connection string del database target.</param>
    /// <exception cref="ArgumentException">Generata quando la connection string è vuota o non valida.</exception>
    public DbManager(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string non valida.", nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    /// <summary>
    /// Esegue una query SQL e restituisce una sequenza tipizzata di risultati.
    /// </summary>
    /// <typeparam name="T">Tipo di mapping del record restituito.</typeparam>
    /// <param name="sql">Istruzione SQL da eseguire.</param>
    /// <param name="parameters">Parametri anonimi opzionali per Dapper.</param>
    /// <returns>Collezione di record mappati sul tipo richiesto.</returns>
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        using var connection = new SqlConnection(_connectionString);
        return await connection.QueryAsync<T>(sql, parameters);
    }

    /// <summary>
    /// Esegue un comando SQL non-query e restituisce il numero di righe coinvolte.
    /// </summary>
    /// <param name="sql">Comando SQL da eseguire.</param>
    /// <param name="parameters">Parametri anonimi opzionali per Dapper.</param>
    /// <returns>Numero di righe interessate dal comando.</returns>
    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        using var connection = new SqlConnection(_connectionString);
        return await connection.ExecuteAsync(sql, parameters);
    }
}
