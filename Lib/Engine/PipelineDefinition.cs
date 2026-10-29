using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace terraform.lib.engine;

public sealed class PipelineDefinition
{
    public Dictionary<string, string>? Variables { get; init; }

    [JsonPropertyName("pipeline")]
    public List<PipelineStep?>? Steps { get; init; }
}

public sealed class PipelineStep
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public bool Enabled { get; init; } = true;
    public JsonElement Config { get; init; }
}

public sealed class PipelineLoadException(string message) : Exception(message);

public static class PipelineLoader
{
    public static async Task<PipelineDefinition> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase))
        {
            throw new PipelineLoadException($"'{path}': usare un file YAML con estensione .yaml o .yml");
        }

        string yaml;

        try
        {
            yaml = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PipelineLoadException($"Impossibile leggere '{path}': {ex.Message}");
        }

        return Parse(yaml, path);
    }

    public static PipelineDefinition Parse(string yaml, string source = "pipeline")
    {
        try
        {
            var value = new DeserializerBuilder()
                .WithAttemptingUnquotedStringTypeDeserialization()
                .Build()
                .Deserialize<object>(yaml);
            if (value is IDictionary root)
            {
                foreach (DictionaryEntry entry in root)
                {
                    if (entry.Key is string key && string.Equals(key, "variables", StringComparison.OrdinalIgnoreCase) && entry.Value is IDictionary variables)
                    {
                        foreach (DictionaryEntry variable in variables)
                        {
                            if (variable.Value is not string)
                            {
                                throw new PipelineLoadException($"{source}: la variabile '{variable.Key}' deve essere una stringa");
                            }
                        }
                    }
                }
            }

            var json = JsonSerializer.Serialize(value);
            return JsonSerializer.Deserialize<PipelineDefinition>(json, ActionConfig.JsonOptions)
                ?? throw new PipelineLoadException($"{source}: il file è vuoto");
        }
        catch (YamlException ex)
        {
            var position = ex.Start.Line >= 0 ? $" (riga {ex.Start.Line}, colonna {ex.Start.Column})" : "";
            throw new PipelineLoadException($"{source}: YAML non valido{position}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new PipelineLoadException($"{source}: configurazione YAML non valida: {ex.Message}");
        }
    }
}
