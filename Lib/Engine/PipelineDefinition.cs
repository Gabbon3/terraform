using System.Text.Json;
using System.Text.Json.Serialization;

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
        string json;

        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PipelineLoadException($"Impossibile leggere '{path}': {ex.Message}");
        }

        return Parse(json, path);
    }

    public static PipelineDefinition Parse(string json, string source = "pipeline")
    {
        try
        {
            return JsonSerializer.Deserialize<PipelineDefinition>(json, ActionConfig.JsonOptions)
                ?? throw new PipelineLoadException($"{source}: il file è vuoto");
        }
        catch (JsonException ex)
        {
            var position = ex.LineNumber is long line ? $" (riga {line + 1}, colonna {ex.BytePositionInLine + 1})" : "";
            throw new PipelineLoadException($"{source}: JSON non valido{position}: {ex.Message}");
        }
    }
}
