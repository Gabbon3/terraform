using terraform.lib.console;

namespace terraform.lib.logger;

// ==============================================================================
//  _                                          
// | |       ___     __ _    __ _    ___   _ __ 
// | |      / _ \   / _` |  / _` |  / _ \ | '__|
// | |___  | (_) | | (_| | | (_| | |  __/ | |   
// |_____|  \___/   \__, |  \__, |  \___| |_|   
//                  |___/   |___/               
//                                              
// Logger generico: scrive su file e console con thread-safety
// ==============================================================================
public sealed class Logger
{
    private readonly string _logFile;
    private readonly object _lockObject = new();
    private int _warningCount;

    /// <summary>Numero di Warning emessi da questo logger, leggibile anche dai worker paralleli.</summary>
    public int WarningCount => Volatile.Read(ref _warningCount);
    
    /// <summary>
    /// Crea un nuovo logger con directory e nome file personalizzati
    /// </summary>
    /// <param name="logDirectory">Directory dove salvare i log</param>
    /// <param name="logFileName">Nome del file di log (senza estensione)</param>
    public Logger(string logDirectory, string logFileName)
    {
        var executableDirectory = AppContext.BaseDirectory;
        var isRootedLogDirectory = Path.IsPathRooted(logDirectory);
        var resolvedLogDirectory = isRootedLogDirectory
            ? logDirectory
            : Path.GetFullPath(Path.Combine(executableDirectory, logDirectory));

        if (!Directory.Exists(resolvedLogDirectory))
        {
            Directory.CreateDirectory(resolvedLogDirectory);
        }

        _logFile = Path.Combine(resolvedLogDirectory, $"{logFileName}.log");
        var displayPath = isRootedLogDirectory
            ? _logFile
            : $".\\{Path.GetRelativePath(executableDirectory, _logFile)}";

        Write($"Logger inizializzato. File di log: {displayPath}", LogLevel.Info);
    }

    /// <summary>
    /// Scrive un messaggio di log su file e console
    /// </summary>
    /// <param name="message">Messaggio da loggare</param>
    /// <param name="level">Livello di log (INFO, SUCCESS, WARN, ERROR)</param>
    /// <param name="isParallel">Se true usa lock per thread-safety</param>
    public void Write(string message, LogLevel level = LogLevel.Info, bool isParallel = false)
    {
        if (level == LogLevel.Warn)
            Interlocked.Increment(ref _warningCount);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logEntry = $"[{timestamp}] [{level}] {message}";

        // Output su console con colori
        var color = level switch
        {
            LogLevel.Info => "Cyan",
            LogLevel.Success => "Green",
            LogLevel.Warn => "Yellow",
            LogLevel.Error => "Red",
            _ => "White"
        };
        
        ConsolePlus.Write($"[{color}]{logEntry}[/]");

        // Scrittura su file con lock opzionale
        if (isParallel)
        {
            lock (_lockObject)
            {
                File.AppendAllText(_logFile, logEntry + Environment.NewLine);
            }
        }
        else
        {
            File.AppendAllText(_logFile, logEntry + Environment.NewLine);
        }
    }
}

public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error
}
