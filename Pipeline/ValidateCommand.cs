using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using terraform.lib.console;
using terraform.lib.engine;
using terraform.lib.utils;

namespace terraform.Pipeline;

/// <summary>Unico argomento del controllo statico: il file della pipeline.</summary>
public sealed class ValidateSettings : CommandSettings
{
    [Description("File pipeline.json da validare senza eseguire azioni o interrogare server")]
    [CommandArgument(0, "<PIPELINE>")]
    public string PipelinePath { get; init; } = "";

    /// <summary>Rifiuta un percorso vuoto prima del caricamento del JSON.</summary>
    public override ValidationResult Validate() => string.IsNullOrWhiteSpace(PipelinePath)
        ? ValidationResult.Error("Il percorso della pipeline e' obbligatorio")
        : ValidationResult.Success();
}

/// <summary>Controllo statico esplicito, con lo stesso preflight usato automaticamente da run.</summary>
public sealed class ValidateCommand : AsyncCommand<ValidateSettings>
{
    /// <summary>Delega al controllo condiviso senza inizializzare logger o servizi di esecuzione.</summary>
    protected override Task<int> ExecuteAsync(CommandContext context, ValidateSettings settings, CancellationToken cancellationToken) =>
        ValidatePipelineAsync(settings.PipelinePath, cancellationToken);

    /// <summary>Carica il solo JSON e restituisce 0 se valido, 1 per errori e 130 su annullamento.</summary>
    public static async Task<int> ValidatePipelineAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var pipeline = await PipelineLoader.LoadAsync(path, cancellationToken);
            // Validate non usa il logger: nessun file di log viene creato per il controllo statico.
            var errors = PipelineRunner.Validate(pipeline, new ActionResolver(ActionCatalog.Create(new ProcessRunner())));
            if (errors.Count == 0)
            {
                ConsolePlus.Write("[Green]OK[/] - Configurazione valida. Nessuna azione eseguita; stato del server non verificato.");
                return RunCommand.ExitOk;
            }
            ConsolePlus.Write($"[Red]Configurazione non valida: {errors.Count} errori.[/]");
            foreach (var error in errors)
                ConsolePlus.WriteListItem(error.AsSpan(), markerColor: ConsoleColor.Red);
            return RunCommand.ExitFailed;
        }
        catch (PipelineLoadException ex)
        {
            ConsolePlus.Write($"[Red]Configurazione non valida:[/] {ex.Message}");
            return RunCommand.ExitFailed;
        }
        catch (OperationCanceledException)
        {
            return RunCommand.ExitCancelled;
        }
    }
}
