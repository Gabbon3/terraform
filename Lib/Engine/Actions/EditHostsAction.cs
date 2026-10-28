using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using terraform.lib.console;
using terraform.lib.logger;

namespace terraform.lib.engine.actions;

public sealed class EditHostsAction : IAction
{
    /// <summary>
    /// Azione <c>HOSTS</c>: allinea le mappature nel file hosts, senza duplicare gli host.
    /// </summary>
    private sealed class Config
    {
        [JsonPropertyName("path")]
        public string? HostsPath { get; init; }

        public List<EntryConfig?>? Entries { get; init; }
    }

    private sealed class EntryConfig
    {
        public string? Ip { get; init; }
        public string? Host { get; init; }
    }

    public string ActionType => "HOSTS";
    public string ActionDescription => "Allinea le mappature hostname/IP nel file hosts locale.";

        public void Help() => ConsolePlus.Write("""
                [Cyan]HOSTS[/] - Allinea le mappature nel file hosts, senza duplicare gli host.
                Esempio di step (JSON con commenti):
                {
                    "id": "hosts", // Obbligatorio e univoco.
                    "type": "HOSTS", // Obbligatorio.
                    "name": "Hosts", // Opzionale: etichetta per i log.
                    "enabled": true, // Opzionale, default true.
                    "config": {
                        "path": "C:\\Windows\\System32\\drivers\\etc\\hosts", // Opzionale: default hosts di sistema.
                        "entries": [ // Obbligatorio: almeno una voce.
                            {
                                "ip": "127.0.0.1", // Obbligatorio: indirizzo IPv4 o IPv6 valido.
                                "host": "{SourceServer}" // Obbligatorio, univoco: niente spazi o #.
                            }
                        ]
                    }
                }
                {SourceServer} va definita in variables. path, se specificato, deve essere completo ed esistere.
                Conserva gli altri alias e scrive solo se necessario. Per il file di sistema servono privilegi adeguati.
                Dry-run: legge il file e mostra le modifiche senza scrivere.
                """);

    public async Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        var settings = ActionConfig.Read<Config>(config);
        var path = settings.HostsPath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : ActionConfig.FullPath(settings.HostsPath, "path");
        var entries = ParseEntries(settings.Entries);

        if (!File.Exists(path))
        {
            return new ActionResult(false, $"file hosts non trovato: {path}");
        }

        var (text, encoding, hasBom) = HostsFile.Decode(await File.ReadAllBytesAsync(path, context.CancellationToken));
        var edit = HostsFile.Apply(text, entries);

        if (edit.Changes.Count == 0)
        {
            return new ActionResult(true, "hosts già allineato");
        }

        foreach (var change in edit.Changes)
        {
            context.Logger.Write(context.IsDryRun ? $"[DRY-RUN] {change}" : $"hosts {change}", LogLevel.Info);
        }

        if (context.IsDryRun)
        {
            return new ActionResult(true, $"{edit.Changes.Count} modifiche da applicare");
        }

        try
        {
            await File.WriteAllBytesAsync(path, HostsFile.Encode(edit.Text, encoding, hasBom), context.CancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return new ActionResult(false, $"accesso negato a {path}: eseguire come amministratore");
        }

        return new ActionResult(true, $"{edit.Changes.Count} modifiche applicate");
    }

    private static List<HostEntry> ParseEntries(List<EntryConfig?>? configured)
    {
        if (configured is not { Count: > 0 })
        {
            throw new ActionConfigException("entries: serve almeno una voce");
        }

        var entries = new List<HostEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < configured.Count; i++)
        {
            var ip = ActionConfig.Required(configured[i]?.Ip, $"entries[{i}].ip").Trim();
            var host = ActionConfig.Required(configured[i]?.Host, $"entries[{i}].host").Trim();

            // Spazi, # o fine riga in un valore creerebbero righe o commenti non voluti nel file.
            var validIp = IPAddress.TryParse(ip, out var address)
                && !ip.Any(char.IsWhiteSpace)
                && (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.Count(c => c == '.') == 3);

            if (!validIp)
            {
                throw new ActionConfigException($"entries[{i}].ip: indirizzo IP non valido '{ip}'");
            }

            if (host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '#'))
            {
                throw new ActionConfigException($"entries[{i}].host: nome host non valido '{host}'");
            }

            if (!seen.Add(host))
            {
                throw new ActionConfigException($"entries[{i}].host: host duplicato '{host}'");
            }

            entries.Add(new HostEntry(ip, host));
        }

        return entries;
    }
}
