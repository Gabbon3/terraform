using System.Text.Json;
using terraform.lib.logger;

namespace terraform.lib.engine.actions;

/// <summary>Configurazione condivisa dalle azioni di clonazione, indipendente dal vecchio migrator.</summary>
internal sealed class WindowsCloneConfig
{
    /// <summary>Server da interrogare con l'identita' del processo terraform, non con le credenziali degli elementi.</summary>
    [ConfigField("Server remoto da cui clonare servizi o task.", Required = true)]
    public string? SourceComputerName { get; init; }
    /// <summary>Selezione opzionale; omettere il filtro seleziona tutti gli elementi remoti.</summary>
    [ConfigField("Filtri facoltativi applicati in AND.")]
    public WindowsCloneFilter? Filter { get; init; }
    /// <summary>Mappa account/password per la creazione locale; dopo la validazione ignora il case degli account.</summary>
    [ConfigField("Mappa di account e password; le password restano sensibili.", Required = true, Sensitive = true, Widget = "map")]
    public Dictionary<string, string?>? Credentials { get; init; }
}

/// <summary>Filtri combinati in AND; all'interno di Names basta che corrisponda uno dei nomi.</summary>
internal sealed class WindowsCloneFilter
{
    /// <summary>Wildcard PowerShell applicata a StartName dei servizi o Principal.UserId dei task.</summary>
    [ConfigField("Wildcard facoltativa per l'account di servizio o del task.")]
    public string? AccountPattern { get; init; }
    /// <summary>Nomi esatti; per i task sono ammessi anche i percorsi completi per distinguere le omonimie.</summary>
    [ConfigField("Nomi esatti facoltativi; per i task è ammesso anche il percorso completo.")]
    public List<string>? Names { get; init; }
}

/// <summary>Validazione e gestione credenziali comuni alle due azioni di clonazione Windows.</summary>
/// <remarks>
/// Non decide cosa creare: gli script delle azioni gestiscono selezione e scrittura.
/// Lo streaming PowerShell e il quoting dei comandi sono gestiti dalle utility generiche in Lib/Utils.
/// </remarks>
internal static class WindowsClone
{
    /// <summary>Valida la config e ricostruisce la mappa credenziali con confronto case-insensitive.</summary>
    /// <param name="config">Config dello step con i placeholder globali gia' risolti dal runner.</param>
    /// <returns>Configurazione validata, con mappa credenziali non nulla.</returns>
    /// <exception cref="ActionConfigException">Campi obbligatori assenti, filtri invalidi o account duplicati.</exception>
    /// <remarks>Una mappa vuota e password vuote sono ammesse; password nulle non lo sono.</remarks>
    public static WindowsCloneConfig ReadConfig(JsonElement config)
    {
        var settings = ActionConfig.Read<WindowsCloneConfig>(config);
        ActionConfig.Required(settings.SourceComputerName, "sourceComputerName");
        if (settings.Credentials is null)
        {
            throw new ActionConfigException("credentials: mappa account/password obbligatoria");
        }

        // Ricostruire il dizionario evita di dipendere dal comparer usato dalla deserializzazione JSON.
        // Due chiavi diverse solo per case sarebbero ambigue: le rifiutiamo, invece di sceglierne una.
        var credentials = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (account, password) in settings.Credentials)
        {
            ActionConfig.Required(account, "credentials: account");
            if (password is null || !credentials.TryAdd(account, password))
            {
                throw new ActionConfigException("credentials: password nulla o account duplicato (ignorando maiuscole/minuscole)");
            }
        }

        // Omissione = nessun vincolo; un filtro esplicitamente vuoto e' invece un errore di config.
        if (settings.Filter?.AccountPattern is { } pattern)
        {
            ActionConfig.Required(pattern, "filter.accountPattern");
        }
        if (settings.Filter?.Names is { } names)
        {
            if (names.Count == 0)
            {
                throw new ActionConfigException("filter.names: lista vuota; omettere il campo per non filtrare per nome");
            }
            for (var i = 0; i < names.Count; i++)
            {
                ActionConfig.Required(names[i], $"filter.names[{i}]");
            }
        }

        return new WindowsCloneConfig
        {
            SourceComputerName = settings.SourceComputerName,
            Filter = settings.Filter,
            Credentials = credentials
        };
    }

    /// <summary>Cerca la password dell'utenza originale; se manca, segnala lo skip senza usare un'altra identita'.</summary>
    /// <param name="context">Contesto usato per il Warning, senza riportare password.</param>
    /// <param name="settings">Configurazione gia' validata da <see cref="ReadConfig"/>.</param>
    /// <param name="account">Account esportato dal server sorgente.</param>
    /// <param name="name">Nome dell'elemento da identificare nel log.</param>
    /// <param name="password">Password trovata, anche vuota; stringa vuota in caso di account assente.</param>
    /// <returns>True solo se l'account e' presente: il chiamante deve saltare l'elemento quando e' false.</returns>
    public static bool TryGetPassword(ActionContext context, WindowsCloneConfig settings, string account, string name, out string password)
    {
        if (settings.Credentials!.TryGetValue(account, out var value))
        {
            password = value!;
            return true;
        }

        context.Logger.Write($"{name}: utenza '{account}' assente in credentials; elemento saltato.", LogLevel.Warn);
        password = "";
        return false;
    }
}
