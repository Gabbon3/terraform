using System.Collections.Frozen;
using System.Text.Json;
using terraform.lib.logger;

namespace terraform.lib.engine;

public record ActionResult(bool Success, string Message = "");

public class ActionContext
{
    public required bool IsDryRun { get; init; }
    public required Logger Logger { get; init; }
    public required VariableEngine Variables { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public interface IAction
{
    string ActionType { get; }
    string ActionDescription { get; }

    /// <summary>Stampa la configurazione JSON, i campi obbligatori e i default dell'azione.</summary>
    void Help();

    // Token che l'azione risolve da sola per ogni elemento: il pre-flight del runner li lascia intatti.
    IReadOnlySet<string> ItemTokens => FrozenSet<string>.Empty;

    /// <summary>Valida la config risolta senza I/O, processi o modifiche; i controlli sullo stato del server restano runtime.</summary>
    /// <exception cref="ActionConfigException">Configurazione non valida.</exception>
    void Validate(JsonElement config);

    Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config);
}
