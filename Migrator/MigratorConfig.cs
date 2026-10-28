namespace terraform.Migrator;

// =================================================================================================
//   ____                    __   _                                        _                        
//  / ___|   ___    _ __    / _| (_)   __ _   _   _   _ __    __ _   ____ (_)   ___    _ __     ___ 
// | |      / _ \  | '_ \  | |_  | |  / _` | | | | | | '__|  / _` | |_  / | |  / _ \  | '_ \   / _ \
// | |___  | (_) | | | | | |  _| | | | (_| | | |_| | | |    | (_| |  / /  | | | (_) | | | | | |  __/
//  \____|  \___/  |_| |_| |_|   |_|  \__, |  \__,_| |_|     \__,_| /___| |_|  \___/  |_| |_|  \___|
//                                    |___/                                                         
// =================================================================================================

/// <summary>
/// Configurazione del Migrator (parametri, percorsi, credenziali)
/// </summary>
public sealed class MigratorConfig
{
    public required string SourceServer { get; init; }
    public required string SourceComputerName { get; init; }
    public required string TargetIp { get; init; }
    public string? StartFrom { get; init; }
    public string? RunOnly { get; init; }
    public bool DryRun { get; init; }
    
    // Log
    public string LogDirectory { get; init; } = @".\MigrazioneServer";
    
    // Step disponibili
    public static readonly string[] AvailableSteps = 
    [
        "Robocopy",
        "SoftwareToInstall",
        "InstallMsi",
        "RegisterDll",
        "CreateServices",
        "CreateTasks",
        "UpdateCleaningConfig",
        "EditHost"
    ];
}
