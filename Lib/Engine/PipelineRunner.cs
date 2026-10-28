using System.Diagnostics;
using System.Text.Json;
using lib.utils;
using terraform.lib.logger;

namespace terraform.lib.engine;

public enum StepStatus { Succeeded, Failed, Skipped, Cancelled, NotRun }

public sealed record StepResult(string Id, string Name, StepStatus Status, string Message, TimeSpan Duration)
{
    public int WarningCount { get; init; }
}

public sealed record PipelineRunResult(IReadOnlyList<string> ValidationErrors, IReadOnlyList<StepResult> Steps, bool Cancelled)
{
    public bool Success => ValidationErrors.Count == 0
        && !Cancelled
        && Steps.All(step => step.Status is StepStatus.Succeeded or StepStatus.Skipped);
}

public sealed class PipelineRunner(ActionResolver resolver, Logger logger)
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed record PlannedStep(PipelineStep Step, string Id, string Name, IAction? Action, JsonElement Config);

    /// <summary>Valida tutti gli step abilitati senza eseguirli e senza controlli di filesystem o rete.</summary>
    public static IReadOnlyList<string> Validate(PipelineDefinition pipeline, ActionResolver resolver)
    {
        var errors = new List<string>();
        var variables = BuildVariables(pipeline, errors);
        BuildPlan(pipeline, variables, errors, resolver);
        return errors;
    }

    public async Task<PipelineRunResult> RunAsync(PipelineDefinition pipeline, bool dryRun, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var variables = BuildVariables(pipeline, errors);
        var plan = BuildPlan(pipeline, variables, errors, resolver);

        if (errors.Count > 0 || variables is null)
        {
            logger.Write($"Pipeline non valida, nessuno step eseguito ({errors.Count} errori):", LogLevel.Error);
            foreach (var error in errors)
            {
                logger.Write($"  - {error}", LogLevel.Error);
            }

            return new PipelineRunResult(errors, [], false);
        }

        var context = new ActionContext
        {
            IsDryRun = dryRun,
            Logger = logger,
            Variables = variables,
            CancellationToken = cancellationToken
        };

        var results = new List<StepResult>(plan.Count);
        var halted = false;
        var cancelled = false;

        if (dryRun)
        {
            logger.Write("DRY-RUN: nessuna modifica verrà applicata", LogLevel.Warn);
        }

        for (var i = 0; i < plan.Count; i++)
        {
            var planned = plan[i];
            var header = $"[{i + 1}/{plan.Count}] {planned.Name} ({planned.Step.Type})";

            if (!planned.Step.Enabled)
            {
                logger.Write($"{header}: disabilitato, saltato", LogLevel.Info);
                results.Add(new StepResult(planned.Id, planned.Name, StepStatus.Skipped, "disabilitato", TimeSpan.Zero));
                continue;
            }

            if (!halted && cancellationToken.IsCancellationRequested)
            {
                halted = cancelled = true;
            }

            if (halted)
            {
                results.Add(new StepResult(planned.Id, planned.Name, StepStatus.NotRun, "", TimeSpan.Zero));
                continue;
            }

            logger.Write(header, LogLevel.Info);
            var warningsBefore = logger.WarningCount;
            var stopwatch = Stopwatch.StartNew();
            ActionResult outcome;

            try
            {
                outcome = await planned.Action!.ExecuteAsync(context, planned.Config);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.Write($"{header}: annullato", LogLevel.Warn);
                results.Add(new StepResult(planned.Id, planned.Name, StepStatus.Cancelled, "annullato", stopwatch.Elapsed)
                {
                    WarningCount = logger.WarningCount - warningsBefore
                });
                halted = cancelled = true;
                continue;
            }
            catch (ActionConfigException ex)
            {
                outcome = new ActionResult(false, ex.Message);
            }
            catch (Exception ex)
            {
                outcome = new ActionResult(false, $"{ex.GetType().Name}: {ex.Message}");
            }

            stopwatch.Stop();
            var elapsed = Formatter.Milliseconds(stopwatch.ElapsedMilliseconds);
            var detail = string.IsNullOrWhiteSpace(outcome.Message) ? "" : $" - {outcome.Message}";
            var warningCount = logger.WarningCount - warningsBefore;

            if (outcome.Success)
            {
                var warningNote = warningCount > 0 ? $" ({warningCount} avvisi)" : "";
                logger.Write($"{header}: completato in {elapsed}{warningNote}{detail}", LogLevel.Success);
                results.Add(new StepResult(planned.Id, planned.Name, StepStatus.Succeeded, outcome.Message, stopwatch.Elapsed)
                {
                    WarningCount = warningCount
                });
            }
            else
            {
                logger.Write($"{header}: FALLITO dopo {elapsed}{detail}", LogLevel.Error);
                results.Add(new StepResult(planned.Id, planned.Name, StepStatus.Failed, outcome.Message, stopwatch.Elapsed)
                {
                    WarningCount = warningCount
                });
                halted = true;
            }
        }

        WriteSummary(results);
        return new PipelineRunResult([], results, cancelled);
    }

    private static VariableEngine? BuildVariables(PipelineDefinition pipeline, List<string> errors)
    {
        try
        {
            return new VariableEngine(pipeline.Variables);
        }
        catch (VariableResolutionException ex)
        {
            errors.AddRange(ex.Errors);
            return null;
        }
    }

    // Raccoglie tutti gli errori prima di eseguire qualsiasi step.
    private static List<PlannedStep> BuildPlan(PipelineDefinition pipeline, VariableEngine? variables, List<string> errors, ActionResolver resolver)
    {
        var plan = new List<PlannedStep>();
        var steps = pipeline.Steps;

        if (steps is null || steps.Count == 0)
        {
            errors.Add("la pipeline non contiene step");
            return plan;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var position = $"pipeline[{i}]";

            if (step is null)
            {
                errors.Add($"{position}: step nullo");
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add($"{position}: 'id' mancante");
            }
            else if (!seenIds.Add(step.Id))
            {
                errors.Add($"{position}: id duplicato '{step.Id}'");
            }

            var id = string.IsNullOrWhiteSpace(step.Id) ? position : step.Id;
            var name = string.IsNullOrWhiteSpace(step.Name) ? id : step.Name;

            if (string.IsNullOrWhiteSpace(step.Type))
            {
                errors.Add($"{id}: 'type' mancante");
                continue;
            }

            if (!step.Enabled)
            {
                plan.Add(new PlannedStep(step, id, name, null, EmptyObject));
                continue;
            }

            if (!resolver.TryResolve(step.Type, out var action))
            {
                errors.Add($"{id}: tipo di azione sconosciuto '{step.Type}' (disponibili: {string.Join(", ", resolver.ActionTypes)})");
                continue;
            }

            // Quel nome in "variables" verrebbe ignorato in silenzio da questo step: il token per-elemento ha la precedenza.
            if (variables is not null)
            {
                foreach (var token in action.ItemTokens.Where(token => variables.Variables.ContainsKey(token)))
                {
                    errors.Add($"{id}: '{token}' è un token per-elemento di '{step.Type}' e non può essere definito in variables");
                }
            }

            var config = step.Config;

            if (config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                config = EmptyObject;
            }
            else if (config.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{id}: 'config' deve essere un oggetto JSON");
                continue;
            }

            if (variables is not null)
            {
                try
                {
                    config = variables.Resolve(config, action.ItemTokens, $"{id}.config");
                }
                catch (VariableResolutionException ex)
                {
                    errors.AddRange(ex.Errors);
                    continue;
                }
            }

            // Stesso contratto per run, dry-run e validate: qui nessuna Action puo' fare I/O.
            if (variables is not null)
            {
                try
                {
                    action.Validate(config);
                }
                catch (ActionConfigException ex)
                {
                    errors.Add($"{id}.config: {ex.Message}");
                    continue;
                }
                catch (VariableResolutionException ex)
                {
                    errors.Add($"{id}.config: {ex.Message}");
                    continue;
                }
            }

            plan.Add(new PlannedStep(step, id, name, action, config));
        }

        return plan;
    }

    private void WriteSummary(List<StepResult> results)
    {
        logger.Write("Riepilogo:", LogLevel.Info);

        foreach (var result in results)
        {
            var (label, level) = result.Status switch
            {
                StepStatus.Succeeded => ("OK", LogLevel.Success),
                StepStatus.Failed => ("ERRORE", LogLevel.Error),
                StepStatus.Cancelled => ("ANNULLATO", LogLevel.Warn),
                StepStatus.Skipped => ("SALTATO", LogLevel.Info),
                _ => ("NON ESEGUITO", LogLevel.Warn)
            };

            var warningNote = result.WarningCount > 0 ? $" ({result.WarningCount} avvisi)" : "";
            logger.Write($"  {label,-12} {result.Id} - {result.Name}{warningNote}", level);
        }
    }
}
