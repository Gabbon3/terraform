using System.Net;
using System.Text;

namespace terraform.lib.engine.actions;

public sealed record HostEntry(string Ip, string Host);

public sealed record HostsEdit(string Text, IReadOnlyList<string> Changes);

/// <summary>Logica pura di modifica del file hosts: lavora sul testo, non sul disco.</summary>
public static class HostsFile
{
    private sealed class Line
    {
        public required string Raw { get; init; }
        public string? Ip { get; init; }
        public List<string> Hosts { get; init; } = [];
        public string? Comment { get; init; }
        public bool Dirty { get; set; }
        public bool Deleted { get; set; }

        public bool Active => Ip is not null;

        public override string ToString() =>
            Dirty ? $"{Ip}\t{string.Join(' ', Hosts)}{(Comment is null ? "" : " " + Comment)}" : Raw;
    }

    /// <summary>UTF-8 (con o senza BOM) e UTF-16 con BOM; un file ANSI non valido in UTF-8 viene letto come Latin1 per non alterarne i byte.</summary>
    public static (string Text, Encoding Encoding, bool HasBom) Decode(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var encoding = reader.CurrentEncoding;
            var preamble = encoding.GetPreamble();
            return (text, encoding, preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble));
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
        }
    }

    public static byte[] Encode(string text, Encoding encoding, bool hasBom)
    {
        var preamble = hasBom ? encoding.GetPreamble() : [];
        return [.. preamble, .. encoding.GetBytes(text)];
    }

    /// <summary>Ogni host richiesto resta su una sola riga attiva con l'IP indicato; commenti, righe vuote e fine riga si conservano.</summary>
    public static HostsEdit Apply(string text, IReadOnlyList<HostEntry> entries)
    {
        var eol = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var rawLines = text.Length == 0 ? [] : text.Split(eol).ToList();
        var endsWithEol = rawLines.Count == 0 || rawLines[^1].Length == 0;

        if (rawLines.Count > 0 && rawLines[^1].Length == 0)
        {
            rawLines.RemoveAt(rawLines.Count - 1);
        }

        var lines = rawLines.Select(Parse).ToList();
        var changes = new List<string>();

        foreach (var entry in entries)
        {
            Ensure(lines, entry, changes);
        }

        if (changes.Count == 0)
        {
            return new HostsEdit(text, changes);
        }

        var body = string.Join(eol, lines.Where(line => !line.Deleted).Select(line => line.ToString()));
        return new HostsEdit(endsWithEol && body.Length > 0 ? body + eol : body, changes);
    }

    private static void Ensure(List<Line> lines, HostEntry entry, List<string> changes)
    {
        var holders = lines.Where(line => !line.Deleted && line.Active && line.Hosts.Contains(entry.Host, StringComparer.OrdinalIgnoreCase)).ToList();
        var exact = holders.FirstOrDefault(line => SameIp(line.Ip!, entry.Ip));
        var stale = holders.Where(line => line != exact).ToList();

        foreach (var line in stale)
        {
            line.Hosts.RemoveAll(host => string.Equals(host, entry.Host, StringComparison.OrdinalIgnoreCase));

            if (line.Hosts.Count == 0)
            {
                line.Deleted = true;
            }
            else
            {
                line.Dirty = true;
            }
        }

        if (exact is null)
        {
            lines.Add(Parse($"{entry.Ip}\t{entry.Host}"));
            changes.Add(stale.Count == 0
                ? $"ADD {entry.Ip}\t{entry.Host}"
                : $"UPDATE {entry.Host}: {string.Join(", ", stale.Select(line => line.Ip).Distinct())} -> {entry.Ip}");
        }
        else if (stale.Count > 0)
        {
            changes.Add($"REMOVE {entry.Host}: {stale.Count} riga/e duplicate o con altro IP");
        }
    }

    private static Line Parse(string raw)
    {
        var hash = raw.IndexOf('#');
        var content = hash >= 0 ? raw[..hash] : raw;
        var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // Una riga con solo l'IP o solo un commento non è una mappatura.
        return tokens.Length < 2
            ? new Line { Raw = raw }
            : new Line { Raw = raw, Ip = tokens[0], Hosts = [.. tokens[1..]], Comment = hash >= 0 ? raw[hash..] : null };
    }

    private static bool SameIp(string left, string right) =>
        IPAddress.TryParse(left, out var a) && IPAddress.TryParse(right, out var b)
            ? a.Equals(b)
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
