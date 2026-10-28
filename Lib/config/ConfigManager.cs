using System.Text.Json;

namespace terraform.lib.config;

public sealed class ConfigManager
{
    private const string DefaultFileName = "migrator_config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static ConfigManager Instance { get; } = new();

    public static AppConfig Global => Instance.Current;

    public AppConfig Current { get; private set; } = new();

    public bool IsLoaded { get; private set; }

    public int ServiceCount => Current.Phase3.Services.Count;

    /// <summary>
    /// Inizializza una nuova istanza del manager di configurazione.
    /// </summary>
    private ConfigManager()
    {
    }

    /// <summary>
    /// Carica la configurazione applicativa da file JSON.
    /// </summary>
    /// <param name="explicitPath">Percorso esplicito del file; se nullo usa la cartella dell'eseguibile.</param>
    /// <returns><c>true</c> se il file è stato caricato correttamente; altrimenti <c>false</c>.</returns>
    public async Task<bool> LoadAsync(string? explicitPath = null)
    {
        var filePath = explicitPath ?? Path.Combine(AppContext.BaseDirectory, DefaultFileName);

        if (!File.Exists(filePath))
        {
            await CreateTemplateFileAsync(filePath);
            IsLoaded = false;
            return false;
        }

        try
        {
            // readalltext poiche il file è piccolo, non serve lo stream
            var json = await File.ReadAllTextAsync(filePath);
            var loadedConfig = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

            if (loadedConfig == null)
            {
                IsLoaded = false;
                return false;
            }

            Current = Normalize(loadedConfig);
            IsLoaded = true;
            return true;
        }
        catch
        {
            IsLoaded = false;
            return false;
        }
    }

    /// <summary>
    /// Recupera la password associata all'utenza di servizio specificata.
    /// </summary>
    /// <param name="username">Nome utenza da cercare nella sezione servizi.</param>
    /// <param name="password">Password trovata se presente.</param>
    /// <returns><c>true</c> se l'utenza è presente in configurazione.</returns>
    public bool TryGetServicePassword(string username, out string password)
    {
        return Current.Phase3.Services.TryGetValue(username, out password!);
    }

    /// <summary>
    /// Crea un file migrator_config.json di esempio nella cartella dell'eseguibile.
    /// </summary>
    /// <returns>Percorso completo del file creato.</returns>
    public async Task<string> InitializeAsync()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        await CreateTemplateFileAsync(filePath);
        return filePath;
    }

    /// <summary>
    /// Normalizza la configurazione garantendo valori non nulli per le sezioni principali.
    /// </summary>
    /// <param name="raw">Configurazione deserializzata dal file.</param>
    /// <returns>Istanza normalizzata pronta all'uso.</returns>
    private static AppConfig Normalize(AppConfig raw)
    {
        return raw with
        {
            Config = (raw.Config ?? new ConfigSection()) with
            {
                Database = raw.Config?.Database ?? new DatabaseConfig()
            },
            Steps = (raw.Steps ?? new StepsSection()) with
            {
                Robocopy = raw.Steps?.Robocopy ?? new StepExecutionConfig(),
                SoftwareToInstall = raw.Steps?.SoftwareToInstall ?? new StepExecutionConfig(),
                InstallMsi = raw.Steps?.InstallMsi ?? new StepExecutionConfig(),
                RegisterDll = raw.Steps?.RegisterDll ?? new StepExecutionConfig(),
                CreateServices = raw.Steps?.CreateServices ?? new StepExecutionConfig(),
                CreateTasks = raw.Steps?.CreateTasks ?? new StepExecutionConfig(),
                UpdateCleaningConfig = raw.Steps?.UpdateCleaningConfig ?? new StepExecutionConfig(),
                EditHost = raw.Steps?.EditHost ?? new StepExecutionConfig()
            },
            Phase1 = (raw.Phase1 ?? new Phase1Section()) with
            {
                Robocopy = raw.Phase1?.Robocopy ?? [],
                RequiredFolders = raw.Phase1?.RequiredFolders ?? []
            },
            Phase2 = (raw.Phase2 ?? new Phase2Section()) with
            {
                FrameworkPaths = raw.Phase2?.FrameworkPaths ?? [],
                DllRegistrationPaths = raw.Phase2?.DllRegistrationPaths ?? [],
                SoftwareToInstall = (raw.Phase2?.SoftwareToInstall ?? new SoftwareToInstallConfig()) with
                {
                    Prerequisites = raw.Phase2?.SoftwareToInstall?.Prerequisites ?? new SoftwarePrerequisitesConfig()
                }
            },
            Phase3 = (raw.Phase3 ?? new Phase3Section()) with
            {
                Services = raw.Phase3?.Services ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            },
            Phase4 = raw.Phase4 ?? new Phase4Section(),
            Phase5 = (raw.Phase5 ?? new Phase5Section()) with
            {
                Hosts = raw.Phase5?.Hosts ?? new Phase5Section().Hosts
            }
        };
    }

    /// <summary>
    /// Crea un file di configurazione di esempio con struttura valida.
    /// </summary>
    /// <param name="filePath">Percorso completo del file template da generare.</param>
    private static async Task CreateTemplateFileAsync(string filePath)
    {
        var template = new AppConfig
        {
            Config = new ConfigSection
            {
                Database = new DatabaseConfig
                {
                    Nimaster = "Server=TEDB01;Database=nimaster;User Id=usrapp-tesi;Password=PASSWORD;TrustServerCertificate=True;",
                    Pulizie = "Server=10.20.51.154,1435;Database=maintainer;User Id=usrapp-tesi;Password=PASSWORD;TrustServerCertificate=True;"
                }
            },
            Steps = new StepsSection
            {
                Robocopy = new StepExecutionConfig { Execute = true },
                SoftwareToInstall = new StepExecutionConfig { Execute = true },
                InstallMsi = new StepExecutionConfig { Execute = true },
                RegisterDll = new StepExecutionConfig { Execute = true },
                CreateServices = new StepExecutionConfig { Execute = true },
                CreateTasks = new StepExecutionConfig { Execute = true },
                UpdateCleaningConfig = new StepExecutionConfig { Execute = true },
                EditHost = new StepExecutionConfig { Execute = true }
            },
            Phase1 = new Phase1Section
            {
                Robocopy =
                [
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\dll_euritmo",
                        Destination = @"D:\gruppotesi\dll_euritmo",
                        Exclude = ["logs", "__old_sv", "___old_sv", "___old", "locks"]
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\xpdf-3.02pl3-win32",
                        Destination = @"D:\xpdf-3.02pl3-win32",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\xpdfbin-win-3.03",
                        Destination = @"D:\xpdfbin-win-3.03",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\blat",
                        Destination = @"D:\gruppotesi\blat",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\dll",
                        Destination = @"D:\gruppotesi\dll",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\wrapper_win32_3.1.2",
                        Destination = @"D:\gruppotesi\wrapper_win32_3.1.2",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\ws_eis_invoices_JXmlUploader",
                        Destination = @"D:\gruppotesi\ws_eis_invoices_JXmlUploader",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\psexec.exe",
                        Destination = @"D:\gruppotesi\psexec.exe",
                        Exclude = []
                    },
                    new RobocopyConfig
                    {
                        Source = @"d$\gruppotesi\java_processo_autorizzativo",
                        Destination = @"D:\gruppotesi\java_processo_autorizzativo",
                        Exclude = []
                    }
                ],
                RequiredFolders =
                [
                    new RequiredFolderConfig
                    {
                        Path = @"D:\elaborazioni",
                        RecursiveDepth = 0
                    },
                    new RequiredFolderConfig
                    {
                        Path = @"C:\e-translator\client",
                        RecursiveDepth = 0
                    },
                    new RequiredFolderConfig
                    {
                        Path = @"D:\Gruppotesi\dll_euritmo\Logs",
                        RecursiveDepth = 1
                    }
                ]
            },
            Phase2 = new Phase2Section
            {
                FrameworkPaths =
                [
                    @"\\tesinas01\eint_stor\gruppotesi\frameworks_dco\e-Integration",
                    @"\\tesinas01\eint_stor\gruppotesi\frameworks_dco\light"
                ],
                DllRegistrationPaths =
                [
                    new DllRegistrationPathConfig
                    {
                        Path = @"D:\gruppotesi\dll_euritmo",
                        Recursive = false
                    },
                    new DllRegistrationPathConfig
                    {
                        Path = @"D:\gruppotesi\dll",
                        Recursive = true
                    }
                ],
                SoftwareToInstall = new SoftwareToInstallConfig
                {
                    VcppInstallerPath = @"\\TEBTCH01\g$\Install\Share\Microsoft Visual C++ 2015 Redistributable Update 3\vc_redist.x64.exe",
                    Prerequisites = new SoftwarePrerequisitesConfig
                    {
                        JavaInstallerPath = @"\\TEBTCH01\g$\Install\Share\Java7\jre-7u80-windows-i586.exe",
                        OfficeIsoPath = @"\\TEBTCH01\g$\Install\Share\Office2000\MSDN_Microsoft_Office_2000_Premium_SR-1_Disc_1_Disc_0226_January_2001.iso",
                        OfficeSp3InstallerPath = @"\\TEBTCH01\g$\Install\Share\Office2000\O2kSp3.exe"
                    }
                }
            },
            Phase3 = new Phase3Section
            {
                Services = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["svc_EINT_Batch_NAS@titesi.local"] = "PASSWORD",
                    ["TITESI\\svc_EINT_Batch_NAS"] = "PASSWORD"
                }
            },
            Phase4 = new Phase4Section(),
            Phase5 = new Phase5Section
            {
                Hosts =
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
                ]
            }
        };

        var json = JsonSerializer.Serialize(template, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }
}
