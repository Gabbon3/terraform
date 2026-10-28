using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace terraform.lib.engine;

public sealed class VariableResolutionException(IReadOnlyList<string> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed partial class VariableEngine
{
    // Valorizzati per elemento da azioni come file-batch-process, quindi mai definibili in "variables".
    public static readonly FrozenSet<string> ReservedNames = new[] { "FilePath", "FileName", "Directory" }.ToFrozenSet();

    // Solo {Nome}: quantificatori regex come {2} o {1,3} e GUID come {8F4B-...} non corrispondono.
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex NamePattern();

    private readonly FrozenDictionary<string, string> _variables;

    public VariableEngine(IReadOnlyDictionary<string, string>? variables = null)
    {
        var errors = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in variables ?? new Dictionary<string, string>())
        {
            if (!NamePattern().IsMatch(name))
            {
                errors.Add($"variables.{name}: nome non valido (lettere, cifre e _, senza iniziare con una cifra)");
            }
            else if (ReservedNames.Contains(name))
            {
                errors.Add($"variables.{name}: nome riservato ai token per-file");
            }
            else if (value is null)
            {
                errors.Add($"variables.{name}: valore nullo");
            }
            else if (TokenPattern().IsMatch(value))
            {
                errors.Add($"variables.{name}: il valore contiene un token, le variabili non si annidano");
            }
            else
            {
                map[name] = value;
            }
        }

        if (errors.Count > 0)
        {
            throw new VariableResolutionException(errors);
        }

        _variables = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> Variables => _variables;

    /// <summary>Sostituisce i token di una stringa; <paramref name="scope"/> ha la precedenza sulle variabili globali.</summary>
    public string Resolve(string template, IReadOnlyDictionary<string, string>? scope = null)
    {
        var errors = new List<string>();
        var result = Substitute(template, scope, null, "$", errors);

        if (errors.Count > 0)
        {
            throw new VariableResolutionException(errors);
        }

        return result;
    }

    /// <summary>
    /// Sostituisce i token nei soli valori stringa del JSON (mai nel testo grezzo né nei nomi di proprietà).
    /// I token in <paramref name="deferredTokens"/> restano intatti per essere risolti dall'azione.
    /// </summary>
    public JsonElement Resolve(JsonElement element, IReadOnlySet<string>? deferredTokens = null, string path = "$")
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return element;
        }

        var errors = new List<string>();
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteResolved(writer, element, deferredTokens, path, errors);
        }

        if (errors.Count > 0)
        {
            throw new VariableResolutionException(errors);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static bool ContainsToken(string value, IReadOnlySet<string> names)
    {
        foreach (var match in TokenPattern().EnumerateMatches(value))
        {
            if (names.Contains(value.Substring(match.Index + 1, match.Length - 2)))
            {
                return true;
            }
        }

        return false;
    }

    private void WriteResolved(Utf8JsonWriter writer, JsonElement element, IReadOnlySet<string>? deferred, string path, List<string> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteResolved(writer, property.Value, deferred, $"{path}.{property.Name}", errors);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WriteResolved(writer, item, deferred, $"{path}[{index++}]", errors);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(Substitute(element.GetString()!, null, deferred, path, errors));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private string Substitute(string value, IReadOnlyDictionary<string, string>? scope, IReadOnlySet<string>? deferred, string path, List<string> errors)
    {
        if (value.IndexOf('{') < 0)
        {
            return value;
        }

        return TokenPattern().Replace(value, match =>
        {
            var name = match.Groups[1].Value;

            if (scope is not null && scope.TryGetValue(name, out var scoped))
            {
                return scoped;
            }

            if (deferred is not null && deferred.Contains(name))
            {
                return match.Value;
            }

            if (_variables.TryGetValue(name, out var globalValue))
            {
                return globalValue;
            }

            errors.Add(ReservedNames.Contains(name)
                ? $"{path}: il token {match.Value} è valido solo in azioni che lo risolvono per elemento"
                : $"{path}: variabile sconosciuta {match.Value}");

            return match.Value;
        });
    }
}
