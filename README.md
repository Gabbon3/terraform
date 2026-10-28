# EDI Migrator

Tool CLI per migrare la configurazione di un server batch EDI da un server sorgente a un nuovo server target.

In pratica automatizza:
- copia strutture/cartelle e file da sorgente (Robocopy)
- installazione prerequisiti e framework MSI
- registrazione DLL (.NET/COM)
- clonazione servizi Windows e task schedulati
- aggiornamento configurazione DB pulizie con nuovo IP
- aggiornamento file hosts per il cutover

## Prerequisiti

- Windows
- Permessi amministratore (obbligatori per diverse operazioni: servizi, DLL, hosts)
- Accesso ai percorsi di rete del server sorgente
- **.NET 9 SDK installato**

Verifica SDK:

```powershell
dotnet --version
```

Se non vedi una versione 9.x, installa .NET 9 SDK.

Nel mio personale FTP è presente il file di installazione: `\\TEBTCH01\g$\ftpsite\tesi_ftp\GabrieleG\DOTNET\dotnet-sdk-9.0.315-win-x64.exe`

## Come usare il software

## 1) Build ed esecuzione

Da root progetto:

```powershell
dotnet build
```

Esecuzione diretta:
```powershell
edi migrator -s TEBTCH04 -c VM1-3LW11N6-29 -t 10.20.51.200
```

Opzionale publish:

```powershell
dotnet publish -c Release -r win-x64
```

## 2) Parametri principali del comando migrator

- `-s, --source-server` Nome server sorgente (es. `TEBTCH04`)
- `-c, --source-computer` Nome computer sorgente per query remote PowerShell
- `-t, --target-ip` Nuovo IP target (usato in fase DB/hosts)
- `--start-from <STEP>` Avvia da uno step specifico
- `--run-only <STEP>` Esegue solo uno step specifico
- `--dry-run` Simula senza modificare
- `--init-config` Crea il template `migrator_config.json`

Step disponibili:
- `Robocopy`
- `SoftwareToInstall`
- `InstallMsi`
- `RegisterDll`
- `CreateServices`
- `CreateTasks`
- `UpdateCleaningConfig`
- `EditHost`

## 3) Esempi pratici

Migrazione completa:

```powershell
dotnet run -- migrator -s TEBTCH04 -c VM1-3LW11N6-29 -t 10.20.51.200
```

Simulazione (consigliata prima della run reale):

```powershell
dotnet run -- migrator -s TEBTCH04 -c VM1-3LW11N6-29 -t 10.20.51.200 --dry-run
```

Riprendi da registrazione DLL:

```powershell
dotnet run -- migrator -s TEBTCH04 -c VM1-3LW11N6-29 -t 10.20.51.200 --start-from RegisterDll
```

Esegui solo aggiornamento DB pulizie:

```powershell
dotnet run -- migrator -s TEBTCH04 -c VM1-3LW11N6-29 -t 10.20.51.200 --run-only UpdateCleaningConfig
```

Esegui solo aggiornamento hosts:

```powershell
dotnet run -- migrator -s TEBTCH04 -c VM1-3LW11N6-29 -t 10.20.51.200 --run-only EditHost
```

## Sezione dedicata: file di configurazione del migrator

Il comando usa un file JSON chiamato `migrator_config.json` nella cartella dell'eseguibile.

## A) Creazione guidata del file

Genera il template:

```powershell
edi migrator --init-config
```

Questo crea un file completo con tutte le sezioni da compilare.

## B) Struttura principale del file

Il file contiene queste macro-sezioni:
- `config.database`
- `steps`
- `1` (fase robocopy e cartelle richieste)
- `2` (framework, DLL, software prerequisiti)
- `3` (mappa utenze servizio -> password)
- `4` (placeholder fase DB)
- `5` (entry hosts)

Esempio sintetico:

```json
{
  "config": {
    "database": {
      "nimaster": "Server=...;Database=nimaster;...",
      "pulizie": "Server=...;Database=maintainer;..."
    }
  },
  "steps": {
    "robocopy": { "execute": true },
    "softwareToInstall": { "execute": true },
    "installMsi": { "execute": true },
    "registerDll": { "execute": true },
    "createServices": { "execute": true },
    "createTasks": { "execute": true },
    "updateCleaningConfig": { "execute": true },
    "editHost": { "execute": true }
  },
  "1": {
    "robocopy": [
      {
        "source": "d$\\gruppotesi\\dll_euritmo",
        "destination": "D:\\gruppotesi\\dll_euritmo",
        "exclude": ["logs", "locks"]
      }
    ],
    "requiredFolders": [
      { "path": "D:\\elaborazioni", "recursiveDepth": 0 }
    ]
  },
  "2": {
    "frameworkPaths": ["\\\\server\\share\\frameworks"],
    "dllRegistrationPaths": [
      { "path": "D:\\gruppotesi\\dll", "recursive": true }
    ],
    "softwareToInstall": {
      "vcppInstallerPath": "\\\\server\\share\\vc_redist.x64.exe",
      "prerequisites": {
        "javaInstallerPath": "\\\\server\\share\\jre-7u80-windows-i586.exe",
        "officeIsoPath": "\\\\server\\share\\office2000.iso",
        "officeSp3InstallerPath": "\\\\server\\share\\O2kSp3.exe"
      }
    }
  },
  "3": {
    "services": {
      "TITESI\\\\svc_EINT_Batch_NAS": "PASSWORD"
    }
  },
  "4": {},
  "5": {
    "hosts": [
      { "ip": "127.0.0.1", "host": "{SourceServer}" },
      { "ip": "10.20.51.44", "host": "dashboard.e-integrationservice.net" }
    ]
  }
}
```

## C) Come compilare correttamente le sezioni più importanti

`config.database`
- `nimaster` e `pulizie` devono essere connection string SQL Server valide.
- `pulizie` è necessaria per lo step `UpdateCleaningConfig`.

`steps`
- Ogni step ha `execute: true|false`.
- Se `false`, lo step viene saltato anche se incluso in run.

`1.robocopy`
- `source`: percorso relativo share sorgente (es. `d$\\...`) o UNC completo.
- `destination`: percorso locale target.
- `exclude`: cartelle da escludere nella copia.

`1.requiredFolders`
- `path`: cartella da garantire sul target.
- `recursiveDepth`: profondità replica struttura da sorgente (`0` solo root, `1+` include sottocartelle, `-1` completo).

`2.frameworkPaths`
- Elenco path dove cercare MSI framework da installare.

`2.dllRegistrationPaths`
- Path con DLL da registrare.
- `recursive: true` per includere sottocartelle.

`2.softwareToInstall`
- Percorsi installer prerequisiti (VC++, Java, Office).

`3.services`
- Dizionario `utenza -> password`.
- Le utenze devono combaciare con quelle rilevate dai servizi/task del server sorgente.

`5.hosts`
- Mappature da applicare al file hosts locale.
- Supporta placeholder `{SourceServer}` nel campo `host`.

## D) Strategia consigliata di utilizzo del config

1. Genera template con `--init-config`.
2. Compila prima `config.database` e `3.services`.
3. Verifica i path di rete in `1` e `2`.
4. Esegui un `--dry-run` completo.
5. Esegui step singoli con `--run-only` per validare le aree più critiche.
6. Avvia la migrazione completa.

## Note operative

- Se il file di configurazione non esiste, il tool crea automaticamente un template ma non procede con dati reali finché non lo compili.
- `--run-only` ha priorità su `--start-from` se entrambi sono presenti.
- Per servizi/task servono credenziali nella sezione `3.services`; senza mappature lo step viene saltato o incompleto.
- Lo step Office SP3 è segnalato come manuale nella logica attuale.

## Troubleshooting rapido

- Errore parametri obbligatori: controlla `-s`, `-c`, `-t`.
- Nessuna modifica in DB: verifica `config.database.pulizie` e `logical_name` uguale al `--source-server`.
- Servizi/task non creati: controlla se utente servizio esiste in `3.services`.
- Problemi hosts: avvia il processo come amministratore.
