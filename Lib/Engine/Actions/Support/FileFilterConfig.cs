using lib.io;

namespace terraform.lib.engine.actions;

/// <summary>Sezione JSON "filter" riusabile da qualunque azione che selezioni file.</summary>
public sealed class FileFilterConfig
{
    public string? Pattern { get; init; }

    // Auto non riconosce le regex: un pattern con * o ? diventa Glob, altrimenti Fixed (sottostringa).
    public PatternMatchType MatchType { get; init; } = PatternMatchType.Auto;

    public bool IgnoreCase { get; init; } = true;

    public bool MatchFullPath { get; init; }

    public string? DateAfter { get; init; }

    public string? DateBefore { get; init; }

    public string? MinSize { get; init; }

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
