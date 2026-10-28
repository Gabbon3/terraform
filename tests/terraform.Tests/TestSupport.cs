using System.Collections.Frozen;
using System.Text.Json;
using terraform.lib.console;
using terraform.lib.engine;
using terraform.lib.logger;

namespace terraform.Tests;

internal static class Json
{
    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

// Cartella temporanea per test, con un Logger che scrive al suo interno.
internal sealed class TestEnvironment : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "terraform-tests", Guid.NewGuid().ToString("N"));

    public Logger Logger { get; }

    public TestEnvironment()
    {
        Directory.CreateDirectory(Root);
        Logger = new Logger(Path.Combine(Root, "logs"), "test");
    }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string CreateFile(string relativePath, string content = "")
    {
        var path = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public ActionContext Context(VariableEngine? variables = null, bool dryRun = false, CancellationToken cancellationToken = default) => new()
    {
        IsDryRun = dryRun,
        Logger = Logger,
        Variables = variables ?? new VariableEngine(),
        CancellationToken = cancellationToken
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeAction(string type, Func<ActionContext, JsonElement, Task<ActionResult>>? handler = null, params string[] itemTokens) : IAction
{
    public List<(JsonElement Config, bool DryRun)> Calls { get; } = [];

    public string ActionType => type;
    public string ActionDescription => $"Test action {type}";

    public void Help() => ConsolePlus.Write(type);

    public IReadOnlySet<string> ItemTokens { get; } = itemTokens.ToFrozenSet();

    public Task<ActionResult> ExecuteAsync(ActionContext context, JsonElement config)
    {
        Calls.Add((config, context.IsDryRun));
        return handler is null ? Task.FromResult(new ActionResult(true, "ok")) : handler(context, config);
    }
}
