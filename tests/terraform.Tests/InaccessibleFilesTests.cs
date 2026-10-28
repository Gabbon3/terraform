using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using terraform.lib.engine;
using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.Tests;

/// <summary>Prove con ACL reali, limitate alla cartella temporanea del test e sempre ripristinate prima del cleanup.</summary>
[SupportedOSPlatform("windows")]
public sealed class InaccessibleFilesTests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    /// <summary>Nega solo lettura/elenco all'utente corrente, conservando il descrittore originale per il ripristino.</summary>
    private sealed class DeniedRead : IDisposable
    {
        private readonly FileSystemInfo _entry;
        private readonly FileSystemSecurity _original;

        public DeniedRead(string path, bool directory)
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("SID utente assente");
            _entry = directory ? new DirectoryInfo(path) : new FileInfo(path);
            _original = directory ? ((DirectoryInfo)_entry).GetAccessControl() : ((FileInfo)_entry).GetAccessControl();
            var denied = directory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
            denied.SetSecurityDescriptorBinaryForm(_original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            denied.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny));
            SetAccess(denied);
        }

        /// <summary>Scrive solo la DACL dell'elemento creato dal test.</summary>
        private void SetAccess(FileSystemSecurity security)
        {
            // Copiamo solo la DACL: owner/SACL richiederebbero privilegi amministrativi non necessari al test.
            FileSystemSecurity access = _entry is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
            access.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            if (_entry is DirectoryInfo directory)
                directory.SetAccessControl((DirectorySecurity)access);
            else
                ((FileInfo)_entry).SetAccessControl((FileSecurity)access);
        }

        public void Dispose() => SetAccess(_original);
    }

    /// <summary>L'helper segnala la cartella negata esatta e visita comunque sorelle e file della root.</summary>
    [Fact]
    public void Enumeration_reports_denied_directories_and_continues_with_siblings()
    {
        var top = _env.CreateFile(@"root\top.txt");
        var sibling = _env.CreateFile(@"root\allowed\ok.txt");
        _env.CreateFile(@"root\denied\hidden.txt");
        var deniedPath = _env.Combine("root", "denied");
        using var denied = new DeniedRead(deniedPath, directory: true);
        var warnings = new List<string>();

        var files = Enumerables.Files(_env.Combine("root"),
            onInaccessible: (path, _) => warnings.Add(path)).Order().ToArray();

        Assert.Equal(new[] { top, sibling }.Order(), files);
        Assert.Equal(deniedPath, Assert.Single(warnings));
        Assert.Throws<UnauthorizedAccessException>(() => Enumerables.Files(deniedPath).ToArray());
    }

    /// <summary>Un'intera root inaccessibile produce un Warning ma non nasconde le altre root.</summary>
    [Fact]
    public async Task Denied_root_does_not_prevent_other_roots_from_running()
    {
        _env.CreateFile(@"denied\hidden.txt");
        _env.CreateFile(@"allowed\ok.txt");
        var deniedPath = _env.Combine("denied");
        using var denied = new DeniedRead(deniedPath, directory: true);
        var native = new FakeProcessRunner();

        var result = await new FileBatchProcessAction(native).ExecuteAsync(_env.Context(), JsonSerializer.SerializeToElement(new
        {
            searchPaths = new[] { deniedPath, _env.Combine("allowed") }, command = "tool", arguments = "{FileName}"
        }));

        Assert.True(result.Success);
        Assert.Equal("ok.txt", Assert.Single(native.Calls).Arguments);
        var log = File.ReadAllText(_env.Combine("logs", "test.log"));
        Assert.Contains(deniedPath + ": accesso negato", log);
        Assert.Equal(1, _env.Logger.WarningCount);
    }

    /// <summary>File illeggibili e sottocartelle negate non avviano comandi e non bloccano gli step successivi, anche in dry-run.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Access_denied_is_a_nonblocking_warning_in_the_pipeline(bool dryRun)
    {
        _env.CreateFile(@"root\allowed\ok.txt");
        var deniedFile = _env.CreateFile(@"root\denied.txt");
        _env.CreateFile(@"root\denied-dir\hidden.txt");
        var deniedDirectory = _env.Combine("root", "denied-dir");
        using var fileAcl = new DeniedRead(deniedFile, directory: false);
        using var directoryAcl = new DeniedRead(deniedDirectory, directory: true);
        var native = new FakeProcessRunner();
        var later = new FakeAction("later");
        var pipeline = PipelineLoader.Parse(JsonSerializer.Serialize(new
        {
            pipeline = new object[]
            {
                new
                {
                    id = "batch", type = "FILE-BATCH-PROCESS",
                    config = new { searchPaths = new[] { _env.Combine("root") }, recurse = true, command = "tool", arguments = "{FileName}" }
                },
                new { id = "later", type = "later" }
            }
        }));
        var runner = new PipelineRunner(new ActionResolver([new FileBatchProcessAction(native), later]), _env.Logger);

        var result = await runner.RunAsync(pipeline, dryRun);

        Assert.True(result.Success);
        Assert.Equal(StepStatus.Succeeded, result.Steps[0].Status);
        Assert.Equal(2, result.Steps[0].WarningCount);
        Assert.Equal(0, result.Steps[1].WarningCount);
        Assert.Single(later.Calls);
        if (dryRun)
            Assert.Empty(native.Calls);
        else
            Assert.Equal("ok.txt", Assert.Single(native.Calls).Arguments);
        var log = File.ReadAllText(_env.Combine("logs", "test.log"));
        Assert.Contains(deniedFile + ": accesso negato", log);
        Assert.Contains(deniedDirectory + ": accesso negato", log);
    }

    /// <summary>L'annullamento viene controllato anche quando non viene prodotto alcun file.</summary>
    [Fact]
    public void Enumeration_observes_cancellation_before_visiting_empty_directories()
    {
        Directory.CreateDirectory(_env.Combine("empty", "child"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Enumerables.Files(_env.Combine("empty"), cancellationToken: cts.Token).ToArray());
    }
}
