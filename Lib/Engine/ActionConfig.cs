using System.Text.Json;
using System.Text.Json.Serialization;

namespace terraform.lib.engine;

public sealed class ActionConfigException(string message) : Exception(message);

public static class ActionConfig
{
    // Una chiave sconosciuta è un errore: un refuso come "enable" non deve lasciare lo step attivo.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static T Read<T>(JsonElement config) where T : class
    {
        try
        {
            return config.Deserialize<T>(JsonOptions) ?? throw new ActionConfigException("config mancante");
        }
        catch (JsonException ex)
        {
            throw new ActionConfigException($"config non valida: {ex.Message}");
        }
    }

    public static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ActionConfigException($"{field}: valore mancante");
        }

        return value;
    }

    // Un percorso relativo dipenderebbe dalla cartella di lavoro, e "\dll" nascerebbe da una variabile vuota.
    public static string FullPath(string? path, string field)
    {
        var value = Required(path, field);

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ActionConfigException($"{field}: il percorso deve essere completo (es. D:\\dir o \\\\server\\share), trovato '{value}'");
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ActionConfigException($"{field}: percorso non valido '{value}': {ex.Message}");
        }
    }
}
