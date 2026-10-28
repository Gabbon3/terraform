using System.Diagnostics.CodeAnalysis;

namespace terraform.lib.engine;

public sealed class ActionResolver
{
    private readonly Dictionary<string, IAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public ActionResolver(IEnumerable<IAction> actions)
    {
        foreach (var action in actions)
        {
            if (!_actions.TryAdd(action.ActionType, action))
            {
                throw new InvalidOperationException($"Azione registrata due volte: '{action.ActionType}'");
            }
        }
    }

    public IEnumerable<string> ActionTypes => _actions.Keys.Order(StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(string type, [NotNullWhen(true)] out IAction? action) => _actions.TryGetValue(type, out action);

    public IAction Resolve(string type) => TryResolve(type, out var action)
        ? action
        : throw new KeyNotFoundException($"Tipo di azione sconosciuto '{type}' (disponibili: {string.Join(", ", ActionTypes)})");
}
