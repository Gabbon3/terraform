using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.logger;
using terraform.lib.utils;

namespace terraform.Pipeline;

public sealed class RunSettings : CommandSettings
{
    [Description("Percorso del file pipeline.json da eseguire")]
    [CommandArgument(0, "<PIPELINE>")]
    public string PipelinePath { get; init; } = "";

    [Description("Simula la pipeline: valida e mostra cosa verrebbe fatto senza modificare nulla")]
    [CommandOption("--dry-run")]
    [DefaultValue(false)]
    public bool DryRun { get; init; }

    public override ValidationResult Validate() =>
        string.IsNullOrWhiteSpace(PipelinePath) ? ValidationResult.Error("Il percorso della pipeline è obbligatorio") : ValidationResult.Success();
}

/// <summary>
/// Comando per eseguire una pipeline definita in un file JSON
/// </summary>
public sealed class RunCommand : AsyncCommand<RunSettings>
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitCancelled = 130;

    /// <summary>
    /// Entry point del comando "run"
    /// </summary>
    /// <param name="context">Contesto del comando</param>
    /// <param name="settings">Impostazioni del comando</param>
    /// <param name="cancellationToken">Token per la cancellazione</param>
    /// <returns>Codice di uscita</returns>
    protected override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        var logger = new Logger("logs", $"pipeline_{DateTime.Now:yyyyMMdd}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += onCancel;

        try
        {
            return await RunPipelineAsync(settings.PipelinePath, settings.DryRun, logger, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    public static async Task<int> RunPipelineAsync(string pipelinePath, bool dryRun, Logger logger, CancellationToken cancellationToken)
    {
        PipelineDefinition pipeline;

        try
        {
            pipeline = await PipelineLoader.LoadAsync(pipelinePath, cancellationToken);
        }
        catch (PipelineLoadException ex)
        {
            logger.Write(ex.Message, LogLevel.Error);
            return ExitFailed;
        }
        catch (OperationCanceledException)
        {
            return ExitCancelled;
        }

        using var provider = BuildServices(logger).BuildServiceProvider();
        var result = await provider.GetRequiredService<PipelineRunner>().RunAsync(pipeline, dryRun, cancellationToken);

        if (result.Cancelled)
        {
            return ExitCancelled;
        }

        return result.Success ? ExitOk : ExitFailed;
    }

    // Registrare qui le nuove azioni: il resolver le raccoglie da solo.
        private static IServiceCollection BuildServices(Logger logger)
        {
            var processRunner = new ProcessRunner();
            var services = new ServiceCollection()
                .AddSingleton(logger)
                .AddSingleton<IProcessRunner>(processRunner);

            foreach (var action in ActionCatalog.Create(processRunner))
            {
                services.AddSingleton<IAction>(action);
            }

            return services
                .AddSingleton<ActionResolver>()
                .AddSingleton<PipelineRunner>();
        }
}
