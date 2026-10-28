using System.Text.Json.Serialization;

namespace terraform.lib.config;

public sealed record AppConfig
{
    [JsonPropertyName("config")]
    public ConfigSection Config { get; init; } = new();

    [JsonPropertyName("steps")]
    public StepsSection Steps { get; init; } = new();

    [JsonPropertyName("1")]
    public Phase1Section Phase1 { get; init; } = new();

    [JsonPropertyName("2")]
    public Phase2Section Phase2 { get; init; } = new();

    [JsonPropertyName("3")]
    public Phase3Section Phase3 { get; init; } = new();

    [JsonPropertyName("4")]
    public Phase4Section Phase4 { get; init; } = new();

    [JsonPropertyName("5")]
    public Phase5Section Phase5 { get; init; } = new();
}

public sealed record StepsSection
{
    [JsonPropertyName("robocopy")]
    public StepExecutionConfig Robocopy { get; init; } = new();

    [JsonPropertyName("softwareToInstall")]
    public StepExecutionConfig SoftwareToInstall { get; init; } = new();

    [JsonPropertyName("installMsi")]
    public StepExecutionConfig InstallMsi { get; init; } = new();

    [JsonPropertyName("registerDll")]
    public StepExecutionConfig RegisterDll { get; init; } = new();

    [JsonPropertyName("createServices")]
    public StepExecutionConfig CreateServices { get; init; } = new();

    [JsonPropertyName("createTasks")]
    public StepExecutionConfig CreateTasks { get; init; } = new();

    [JsonPropertyName("updateCleaningConfig")]
    public StepExecutionConfig UpdateCleaningConfig { get; init; } = new();

    [JsonPropertyName("editHost")]
    public StepExecutionConfig EditHost { get; init; } = new();

    public bool ShouldExecute(string step)
    {
        return step switch
        {
            "Robocopy" => Robocopy.Execute,
            "SoftwareToInstall" => SoftwareToInstall.Execute,
            "InstallMsi" => InstallMsi.Execute,
            "RegisterDll" => RegisterDll.Execute,
            "CreateServices" => CreateServices.Execute,
            "CreateTasks" => CreateTasks.Execute,
            "UpdateCleaningConfig" => UpdateCleaningConfig.Execute,
            "EditHost" => EditHost.Execute,
            _ => true
        };
    }
}

public sealed record StepExecutionConfig
{
    [JsonPropertyName("execute")]
    public bool Execute { get; init; } = true;
}

public sealed record ConfigSection
{
    [JsonPropertyName("database")]
    public DatabaseConfig Database { get; init; } = new();
}

public sealed record DatabaseConfig
{
    [JsonPropertyName("nimaster")]
    public string Nimaster { get; init; } = string.Empty;

    [JsonPropertyName("pulizie")]
    public string Pulizie { get; init; } = string.Empty;
}

public sealed record Phase1Section
{
    [JsonPropertyName("robocopy")]
    public List<RobocopyConfig> Robocopy { get; init; } = [];

    [JsonPropertyName("requiredFolders")]
    public List<RequiredFolderConfig> RequiredFolders { get; init; } = [];
}

public sealed record RequiredFolderConfig
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("recursiveDepth")]
    public int RecursiveDepth { get; init; }
}

public sealed record Phase2Section
{
    [JsonPropertyName("frameworkPaths")]
    public List<string> FrameworkPaths { get; init; } = [];

    [JsonPropertyName("dllRegistrationPaths")]
    public List<DllRegistrationPathConfig> DllRegistrationPaths { get; init; } = [];

    [JsonPropertyName("softwareToInstall")]
    public SoftwareToInstallConfig SoftwareToInstall { get; init; } = new();
}

public sealed record DllRegistrationPathConfig
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }
}

public sealed record SoftwareToInstallConfig
{
    [JsonPropertyName("vcppInstallerPath")]
    public string VcppInstallerPath { get; init; } = string.Empty;

    [JsonPropertyName("prerequisites")]
    public SoftwarePrerequisitesConfig Prerequisites { get; init; } = new();
}

public sealed record SoftwarePrerequisitesConfig
{
    [JsonPropertyName("javaInstallerPath")]
    public string JavaInstallerPath { get; init; } = string.Empty;

    [JsonPropertyName("officeIsoPath")]
    public string OfficeIsoPath { get; init; } = string.Empty;

    [JsonPropertyName("officeSp3InstallerPath")]
    public string OfficeSp3InstallerPath { get; init; } = string.Empty;
}

public sealed record Phase3Section
{
    [JsonPropertyName("services")]
    public Dictionary<string, string> Services { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record Phase4Section
{
}

public sealed record Phase5Section
{
    [JsonPropertyName("hosts")]
    public List<HostEntryConfig> Hosts { get; init; } =
    [
        new HostEntryConfig
        {
            Ip = "127.0.0.1",
            Host = "{SourceServer}"
        },
        new HostEntryConfig
        {
            Ip = "10.20.51.44",
            Host = "dashboard.e-integrationservice.net"
        }
    ];
}

public sealed record HostEntryConfig
{
    [JsonPropertyName("ip")]
    public string Ip { get; init; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;
}

public sealed record RobocopyConfig
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("destination")]
    public string Destination { get; init; } = string.Empty;

    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; init; } = [];
}
