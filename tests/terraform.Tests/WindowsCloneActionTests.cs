using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.Tests;

/// <summary>Verifica il flow script / NDJSON / comando locale senza toccare servizi o task reali.</summary>
public sealed class WindowsCloneActionTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private const string Account = @"DOMAIN\svc_batch";
    private const string Password = "secret'\"$; &";
    private const string BinaryPath = "\"C:\\Program Files\\Batch\\batch.exe\" --name \"one two\"";
    private const string TaskXml = """
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><TimeTrigger><Enabled>true</Enabled></TimeTrigger></Triggers>
          <Settings><Enabled>true</Enabled></Settings><Actions/>
        </Task>
        """;

    /// <summary>Elimina i temporanei isolati del test.</summary>
    public void Dispose() => _env.Dispose();

    /// <summary>Servizio esportato con comando quotato e argomenti.</summary>
    private static object Service(string name = "Batch") => new { Name = name, DisplayName = "Batch service", PathName = BinaryPath, StartName = Account };

    /// <summary>Task esportato con percorso distinto dal nome.</summary>
    private static object ScheduledTask(string xml = TaskXml) => new { TaskName = "Import", TaskPath = @"\Batch\", Principal = Account, Xml = xml };

    /// <summary>Config con case account differente e server contenente un apostrofo.</summary>
    private static object Config(object? filter = null) => new
    {
        sourceComputerName = "source';not-code", filter,
        credentials = new Dictionary<string, string> { [Account.ToUpperInvariant()] = Password }
    };

    /// <summary>Simula lo stream remoto lasciando all'azione la deserializzazione riga per riga.</summary>
    private static async IAsyncEnumerable<string> Lines(object[] items, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return JsonSerializer.Serialize(item);
        }
    }

    /// <summary>Runner con query NDJSON finta e handler per i comandi nativi locali.</summary>
    private static FakeProcessRunner Runner(object[] items, Func<string, string, CancellationToken, Task<ProcessResult>>? handler = null) =>
        new(handler ?? ((_, _, _) => Task.FromResult(new ProcessResult(1060, "", ""))),
            (_, _, ct) => Lines(items, ct));

    /// <summary>Esegue una delle due azioni nel contesto isolato del test.</summary>
    private Task<ActionResult> Run(bool tasks, IProcessRunner runner, object? config = null, bool dryRun = false, CancellationToken ct = default)
    {
        IAction action = tasks ? new CreateTasksAction(runner) : new CreateServicesAction(runner);
        return action.ExecuteAsync(_env.Context(dryRun: dryRun, cancellationToken: ct), JsonSerializer.SerializeToElement(config ?? Config()));
    }

    /// <summary>Legge il log per verificare warning e assenza di password.</summary>
    private string Log() => File.ReadAllText(_env.Combine("logs", "test.log"));

    /// <summary>Verifica sc.exe, comando originale e credenziali assegnate nella stessa creazione Disabled.</summary>
    [Fact]
    public async Task Services_preserve_command_and_identity()
    {
        var runner = Runner([Service()], (_, args, _) => Task.FromResult(
            new ProcessResult(args.StartsWith("query ") ? 1060 : 0, "", "")));
        var result = await Run(false, runner);
        Assert.True(result.Success);
        Assert.Equal(new[] { "powershell.exe", "sc.exe", "sc.exe" }, runner.Calls.Select(call => call.FileName));
        Assert.Equal(new[] { "create", "Batch", "binPath=", BinaryPath, "DisplayName=", "Batch service",
            "start=", "disabled", "obj=", Account, "password=", Password },
            ProcessUtilsTests.ParseArguments(runner.Calls.Last().Arguments));
        Assert.DoesNotContain(Password, Log());
    }

    /// <summary>Solo 1060 significa assenza: non mascherare errori di accesso come servizio mancante.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Existing_services_are_skipped_but_query_errors_fail(int code)
    {
        var runner = Runner([Service()], (_, _, _) => Task.FromResult(new ProcessResult(code, "", "")));
        if (code == 0)
        {
            var result = await Run(false, runner);
            Assert.Equal("0 servizi creati, 1 saltati", result.Message);
        }
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(false, runner));
        Assert.Equal(2, runner.Calls.Count);
    }

    /// <summary>Account assente: warning e skip, senza creare un elemento sotto un'altra identita'.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_credentials_warn_and_skip(bool tasks)
    {
        var runner = Runner([tasks ? ScheduledTask() : Service()]);
        var result = await Run(tasks, runner, new { sourceComputerName = "source", credentials = new { } });
        Assert.True(result.Success);
        Assert.Contains("1 saltati", result.Message);
        Assert.Contains("assente in credentials", Log());
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("create", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Controlla XML, trigger, quoting e cleanup mentre schtasks legge il temporaneo.</summary>
    [Theory]
    [InlineData(TaskXml)]
    [InlineData("<Task><Triggers><TimeTrigger><Enabled>true</Enabled></TimeTrigger></Triggers><Settings/><Actions/></Task>")]
    [InlineData("<Task><Triggers><TimeTrigger><Enabled>true</Enabled></TimeTrigger></Triggers><Actions/></Task>")]
    public async Task Tasks_use_schtasks_and_disable_only_general_settings(string xml)
    {
        string? xmlPath = null;
        var runner = Runner([ScheduledTask(xml)], (file, args, _) =>
        {
            Assert.Equal("schtasks.exe", file);
            var arguments = ProcessUtilsTests.ParseArguments(args);
            xmlPath = arguments[4];
            Assert.Equal(new[] { "/Create", "/TN", @"\Batch\Import", "/XML", xmlPath, "/RU", Account, "/RP", Password, "/F" }, arguments);
            var document = XDocument.Load(xmlPath);
            var ns = document.Root!.Name.Namespace;
            Assert.Equal("false", document.Root.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value);
            Assert.Equal("true", document.Root.Element(ns + "Triggers")!.Descendants(ns + "Enabled").Single().Value);
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        Assert.True((await Run(true, runner)).Success);
        Assert.NotNull(xmlPath);
        Assert.False(File.Exists(xmlPath));
        Assert.DoesNotContain(Password, Log());
    }

    /// <summary>Errore o annullamento della registrazione: il temporaneo va eliminato e il ciclo non prosegue.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_failure_or_cancellation_cleans_xml(bool cancel)
    {
        string? path = null;
        var runner = Runner([ScheduledTask(), ScheduledTask()], (_, args, _) =>
        {
            path = ProcessUtilsTests.ParseArguments(args)[4];
            if (cancel) throw new OperationCanceledException();
            return Task.FromResult(new ProcessResult(5, "", Password));
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(true, runner));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(true, runner));
            Assert.DoesNotContain(Password, error.Message);
        }
        Assert.NotNull(path);
        Assert.False(File.Exists(path));
        Assert.Equal(2, runner.Calls.Count);
    }

    /// <summary>XML vuoto viene saltato; XML e NDJSON invalidi interrompono invece lo step.</summary>
    [Fact]
    public async Task Empty_xml_warns_but_invalid_data_fails()
    {
        Assert.True((await Run(true, Runner([ScheduledTask("")]))).Success);
        Assert.Contains("XML vuoto", Log());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(true, Runner([ScheduledTask("<wrong/>")])));
        await Assert.ThrowsAsync<JsonException>(() => Run(true, Runner(["not a task DTO"])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(true, Runner([null!])));
    }

    /// <summary>Il dry-run interroga la sorgente anche senza risultati, ma non scrive sul target.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dry_run_and_empty_selection_do_not_write(bool tasks)
    {
        var runner = Runner([]);
        Assert.True((await Run(tasks, runner, dryRun: true)).Success);
        Assert.Single(runner.Calls);
        Assert.Equal("powershell.exe", runner.Calls.Single().FileName);
        var result = await Run(tasks, runner);
        Assert.True(result.Success);
        Assert.StartsWith("0 ", result.Message);
        Assert.Equal(2, runner.Calls.Count);
    }

    /// <summary>Preview per elemento, conteggi corretti e nessuna registrazione, anche con elementi non clonabili.</summary>
    [Fact]
    public async Task Task_dry_run_reports_plan_and_skips_without_registering()
    {
        var runner = Runner([
            ScheduledTask(),
            new { TaskName = "NoCredential", TaskPath = @"\Batch\", Principal = "other", Xml = TaskXml },
            new { TaskName = "EmptyXml", TaskPath = @"\Batch\", Principal = Account, Xml = "" }
        ], (_, _, _) => throw new InvalidOperationException("Il dry-run non deve avviare schtasks."));

        var result = await Run(true, runner, dryRun: true);

        Assert.True(result.Success);
        Assert.Equal("3 task trovati, 1 da registrare, 2 saltati", result.Message);
        Assert.Single(runner.Calls);
        var log = Log();
        Assert.Contains(@"[DRY-RUN] Task \Batch\Import", log);
        Assert.Contains(Account, log);
        Assert.Contains("credenziale presente nella mappa", log);
        Assert.Contains("XML verificato", log);
        Assert.Contains("Settings/Enabled=false, trigger invariati", log);
        Assert.Contains("sovrascritto (/F)", log);
        Assert.Contains("assente nella mappa", log);
        Assert.Contains("XML vuoto, saltato", log);
        Assert.DoesNotContain(@"Task \Batch\EmptyXml: verrebbe registrato", log);
        Assert.DoesNotContain(@"Task \Batch\NoCredential: verrebbe registrato", log);
        Assert.DoesNotContain(Password, log);
    }

    /// <summary>Il dry-run non promette registrazioni con XML che fallirebbero nella run reale.</summary>
    [Theory]
    [InlineData("<wrong/>")]
    [InlineData("<Task>")]
    public async Task Task_dry_run_rejects_invalid_xml(string xml)
    {
        var runner = Runner([ScheduledTask(xml)]);
        if (xml == "<wrong/>")
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(true, runner, dryRun: true));
        else
            await Assert.ThrowsAsync<System.Xml.XmlException>(() => Run(true, runner, dryRun: true));
        Assert.Single(runner.Calls);
        Assert.DoesNotContain("verrebbe registrato", Log());
    }

    /// <summary>La preview dei servizi esegue solo query e segnala credenziali mancanti e servizi gia' esistenti.</summary>
    [Fact]
    public async Task Service_dry_run_checks_before_reporting_creation()
    {
        var runner = Runner([
            Service(),
            Service("Existing"),
            new { Name = "NoCredential", DisplayName = "Missing credential", PathName = BinaryPath, StartName = "other" }
        ], (file, args, _) =>
        {
            Assert.Equal("sc.exe", file);
            Assert.StartsWith("query ", args);
            return Task.FromResult(new ProcessResult(args.Contains("Existing") ? 0 : 1060, "", ""));
        });

        var result = await Run(false, runner, dryRun: true);

        Assert.True(result.Success);
        Assert.Equal("3 servizi trovati, 1 da creare, 2 saltati", result.Message);
        Assert.Equal(4, runner.Calls.Count);
        var log = Log();
        Assert.Contains("[DRY-RUN] Servizio Batch ('Batch service')", log);
        Assert.Contains(BinaryPath, log);
        Assert.Contains(Account, log);
        Assert.Contains("DISABLED", log);
        Assert.Contains("esiste gia', saltato", log);
        Assert.Contains("assente in credentials", log);
        Assert.DoesNotContain(Password, log);
    }

    /// <summary>La validazione precede qualsiasi query o comando locale.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_configuration_fails_before_processes(bool tasks)
    {
        var runner = Runner([]);
        object[] invalid =
        [
            new { credentials = new { } }, new { sourceComputerName = "source" },
            Config(new { names = Array.Empty<string>() }), Config(new { accountPattern = "" }),
            Config(new { names = new string?[] { null } }),
            new { sourceComputerName = "source", credentials = new { }, unknown = true },
            new { sourceComputerName = "source", credentials = new Dictionary<string, string?> { ["a"] = null } },
            new { sourceComputerName = "source", credentials = new Dictionary<string, string> { ["a"] = "x", ["A"] = "y" } }
        ];
        foreach (var config in invalid)
            await Assert.ThrowsAsync<ActionConfigException>(() => Run(tasks, runner, config));
        Assert.Empty(runner.Calls);
    }

    /// <summary>Se il consumer termina al primo errore, lo stream remoto viene comunque chiuso.</summary>
    [Fact]
    public async Task Service_creation_failure_stops_and_disposes_the_stream()
    {
        var disposed = false;
        async IAsyncEnumerable<string> Stream()
        {
            try
            {
                await Task.CompletedTask;
                yield return JsonSerializer.Serialize(Service());
                throw new InvalidOperationException("Non deve leggere il secondo elemento");
            }
            finally { disposed = true; }
        }
        var runner = new FakeProcessRunner((_, args, _) => Task.FromResult(new ProcessResult(args.StartsWith("query") ? 1060 : 5, "", "")),
            (_, _, _) => Stream());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(false, runner));
        Assert.Contains("exit code 5", error.Message);
        Assert.True(disposed);
    }

    /// <summary>Esegue il vero PowerShell con i comandi di rete sostituiti; i comandi locali restano finti.</summary>
    /// <param name="tasks">True per task, false per servizi.</param>
    /// <param name="selection">0: tutti; 1: account; 2: nome esatto; 3: intersezione senza corrispondenze.</param>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task Scripts_stream_ndjson_and_apply_filters(bool tasks, int selection)
    {
        var fixture = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Services = new[] { Service(), new { Name = "Other", DisplayName = "Other", PathName = BinaryPath, StartName = "other" } },
            Tasks = new[] { ScheduledTask(), new { TaskName = "Other", TaskPath = @"\Other\", Principal = "other", Xml = TaskXml } }
        }));
        // Le funzioni oscurano i cmdlet reali: nessun server viene contattato, ma sintassi e filtri sono eseguiti davvero.
        var prelude = $"$script:fixture = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{fixture}')))\n" + """
            function Get-CimInstance { param($ClassName, $ComputerName) $script:fixture.Services }
            function Invoke-Command { param($ComputerName, $ScriptBlock) & $ScriptBlock }
            function Get-ScheduledTask { $script:fixture.Tasks | ForEach-Object {
                [PSCustomObject]@{ TaskName = $_.TaskName; TaskPath = $_.TaskPath; Principal = @{ UserId = $_.Principal } }
            } }
            function Export-ScheduledTask { param($TaskName, $TaskPath)
                ($script:fixture.Tasks | Where-Object { $_.TaskName -eq $TaskName -and $_.TaskPath -eq $TaskPath }).Xml + (' ' * 40000)
            }

            """;
        var runner = new FakeProcessRunner((_, args, _) => Task.FromResult(new ProcessResult(args.StartsWith("query") ? 1060 : 0, "", "")),
            (file, args, ct) => new ProcessRunner().StreamAsync(file,
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + ProcessUtilsTests.Decode(args))), ct));
        object? filter = selection switch
        {
            1 => new { accountPattern = @"*\svc_*" },
            2 => new { names = new[] { tasks ? @"\Batch\Import" : "batch" } },
            3 => new { accountPattern = "*svc_*", names = new[] { "NoMatch" } },
            _ => null
        };
        var result = await Run(tasks, runner, Config(filter));
        Assert.True(result.Success);
        Assert.StartsWith(selection == 3 ? "0 " : "1 ", result.Message);
        Assert.Contains(selection == 0 ? "1 saltati" : "0 saltati", result.Message);
    }

    /// <summary>Verifica wiring reale e risoluzione delle variabili globali prima della query.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_resolves_global_variables(bool tasks)
    {
        var runner = Runner([tasks ? ScheduledTask() : Service()], (_, args, _) =>
            Task.FromResult(new ProcessResult(args.StartsWith("query") ? 1060 : 0, "", "")));
        var definition = PipelineLoader.Parse(Yaml.Serialize(new
        {
            variables = new { SourceComputerName = "SOURCE", Password },
            pipeline = new[] { new { id = "clone", type = tasks ? "CREATE-TASKS" : "CREATE-SERVICES",
                config = new { sourceComputerName = "{SourceComputerName}", credentials = new Dictionary<string, string> { [Account] = "{Password}" } } } }
        }));
        var result = await new PipelineRunner(new ActionResolver(ActionCatalog.Create(runner)), _env.Logger).RunAsync(definition, false);
        Assert.True(result.Success);
        Assert.Contains("'SOURCE'", ProcessUtilsTests.Decode(runner.Calls.First().Arguments));
        Assert.DoesNotContain(Password, ProcessUtilsTests.Decode(runner.Calls.First().Arguments));
        Assert.Contains(Password, ProcessUtilsTests.ParseArguments(runner.Calls.Last().Arguments));
    }

    /// <summary>L'esempio non deve abilitare accidentalmente operazioni amministrative.</summary>
    [Fact]
    public async Task Sample_clone_steps_are_disabled()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\Pipeline\pipeline.sample.yaml"));
        var pipeline = await PipelineLoader.LoadAsync(path);
        Assert.Equal(4, pipeline.Steps!.Count);
        Assert.All(pipeline.Steps.Skip(2), step => Assert.False(step!.Enabled));
    }
}
