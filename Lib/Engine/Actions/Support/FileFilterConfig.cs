using lib.io;

namespace terraform.lib.engine.actions;

/// <summary>Sezione JSON "filter" riusabile da qualunque azione che selezioni file.</summary>
public sealed class FileFilterConfig
{
    [ConfigField("Pattern facoltativo per selezionare i file.")]
    public string? Pattern { get; init; }

    // Auto non riconosce le regex: un pattern con * o ? diventa Glob, altrimenti Fixed (sottostringa).
    [ConfigField("Interpretazione del pattern.", DefaultJson = "\"Auto\"")]
    public PatternMatchType MatchType { get; init; } = PatternMatchType.Auto;

    [ConfigField("Ignora maiuscole e minuscole.", DefaultJson = "true")]
    public bool IgnoreCase { get; init; } = true;

    [ConfigField("Applica il pattern al percorso completo anziché al solo nome file.", DefaultJson = "false")]
    public bool MatchFullPath { get; init; }

    [ConfigField("Data minima; supporta date relative o assolute.")]
    public string? DateAfter { get; init; }

    [ConfigField("Data massima; supporta date relative o assolute.")]
    public string? DateBefore { get; init; }

    [ConfigField("Dimensione minima (byte o suffissi k, m, g, t).")]
    public string? MinSize { get; init; }

    [ConfigField("Dimensione massima (byte o suffissi k, m, g, t).")]
    public string? MaxSize { get; init; }

    /// <returns>Il filtro, oppure null se la config non pone restrizioni.</returns>
    public FileSystemFilter? Build()
    {
        var options = new FileFilterFactory.FilterOptions(
            Pattern: string.IsNullOrEmpty(Pattern) ? null : Pattern,
            MatchType: MatchType,
            IgnoreCase: IgnoreCase,
            DateAfter: ParseDate(DateAfter, "filter.dateAfter"),
            DateBefore: ParseDate(DateBefore, "filter.dateBefore"),
            MinSize: ParseSize(MinSize, "filter.minSize"),
            MaxSize: ParseSize(MaxSize, "filter.maxSize"),
            MatchFullPath: MatchFullPath);

        try
        {
            return FileFilterFactory.CreateFilter(options);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new ActionConfigException($"filter.pattern: regex non valida o non supportata (NonBacktracking: niente lookaround né backreference): {ex.Message}");
        }
    }

    private static RelativeDateTime? ParseDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (RelativeDateTime.TryParseRelativeDate(value, out RelativeDateTime result))
        {
            return result;
        }

        throw new ActionConfigException($"{field}: valore non valido '{value}' (esempi: 12d, 5h, 2024-01-15, 60d:c)");
    }

    private static RelativeSize? ParseSize(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return RelativeSize.Parse(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ActionConfigException($"{field}: {ex.Message} ('{value}', esempi: 500, 10k, 5m, 2g)");
        }
    }
}
