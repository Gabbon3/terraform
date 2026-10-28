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

# Pipeline engine

Il comando `run` esegue una pipeline descritta in un file JSON, step dopo step, con azioni generiche e configurabili. È indipendente dal comando `migrator`.

```powershell
edi run pipeline.json --dry-run   # valida e mostra cosa verrebbe fatto, senza modificare nulla
edi run pipeline.json
```

Un esempio funzionante è in `Pipeline/pipeline.sample.json`. Il file ammette commenti e virgole finali.

## Struttura

- `variables`: coppie nome/valore, usabili come `{Nome}` (maiuscole e minuscole contano) in qualunque valore stringa delle config. I valori non possono contenere altri token.
- `pipeline`: elenco di step con `id` (univoco), `name` (opzionale), `type`, `enabled` (default `true`) e `config`.
- Prima di eseguire qualsiasi step il motore controlla tutta la pipeline (id, tipi, token) e riporta tutti gli errori insieme. Gli step con `enabled: false` non vengono validati per tipo.
- Gli step girano in ordine; al primo fallimento la pipeline si ferma. La config di ogni azione viene validata quando parte il suo step: `--dry-run` la esercita tutta.
- Tutti i percorsi devono essere completi (`D:\dir` o `\\server\share`); un percorso relativo è un errore.
- Exit code: `0` successo, `1` errore o pipeline non valida, `130` annullata con Ctrl+C. I log sono in `logs\pipeline_<data>.log` accanto all'eseguibile.

## Azioni

**`REQUIRED-FOLDERS`**: crea le cartelle di `paths` (anche i genitori). Idempotente; fallisce se esiste un file con lo stesso nome.

**`FILE-BATCH-PROCESS`**: esegue un comando per ogni file trovato.
- `searchPaths` (obbligatorio), `recurse` (default `false`), `maxConcurrency` (default: numero di CPU), `command`, `arguments`.
- `filter` (opzionale): `pattern`, `matchType` (`Auto`, `Regex`, `Fixed`, `Glob`), `ignoreCase` (default `true`), `matchFullPath`, `dateAfter`, `dateBefore` (es. `30d`, `12h`, `2024-01-15`, `60d:c`), `minSize`, `maxSize` (es. `10k`, `5m`).
- `matchType: Auto` non riconosce le regex: un pattern con `*` o `?` diventa Glob, altrimenti è una sottostringa. Per le regex usare `Regex` (niente lookaround né backreference).
- In `arguments` si possono usare `{FilePath}`, `{FileName}` (con estensione) e `{Directory}`; altrove sono un errore. Il comando parte senza shell: i nomi file non vengono interpretati.
- Include file nascosti e di sistema, non segue le junction. `searchPaths` duplicati o annidati con `recurse` attivo sono rifiutati.
- Tutti i file vengono elaborati; lo step fallisce se almeno un comando termina con exit code diverso da 0.

**`HOSTS`**: `entries` (`ip`, `host`) e `path` opzionale (default: hosts di sistema). Ogni host resta su una sola riga attiva con l'IP indicato; gli altri alias, i commenti e la codifica del file si conservano. Scrive solo se qualcosa cambia; serve l'amministratore.

**`ROBOCOPY`**: `source`, `destination`, `files`, `excludeDirs`, `excludeFiles`, `mirror`, `threads`, `retries` (1), `waitSeconds` (1), `copyOptions`, `extraArguments`.
- I default sono quelli del migrator. Per una cartella: `/MIR /MT:16 /COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP`, più `/SECFIX` se la destinazione esiste già. Quindi `mirror` vale `true` se omesso e **elimina dalla destinazione ciò che non è nella sorgente**: `mirror: false` toglie `/MIR`.
- Se `source` è un file, `destination` è il percorso completo del file copiato e si usano solo `/COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP` (e `/SECFIX`): niente `/MIR` né `/MT`, e `files`, `excludeDirs`, `excludeFiles` non sono ammessi.
- `copyOptions` sostituisce `/COPYALL /B /DCOPY:DAT` (stringa vuota per nessuna); `extraArguments` si aggiunge in coda senza toccare i default.
- Exit code di robocopy da 0 a 7 sono successo, da 8 in su fallimento.
- Con `--dry-run` aggiunge `/L`: non copia né cancella nulla ma elenca cosa succederebbe.
- Le operazioni che cancellano (`mirror`, `/MIR`, `/PURGE`, `/MOVE`) sono rifiutate se la destinazione è la radice di un'unità o di una share: per copiare lì servono `mirror: false` e nessuno di quegli switch.

**`EXECUTE-PROCESS`**: esegue in sequenza una lista di programmi, tipicamente installer `.exe`.
- `items`: ciascuno con `path` (completo), `name` (etichetta nei log), `arguments`, `successExitCodes` e `ignoreIfNotFound`.
- `defaultArguments` vale per gli item senza `arguments` propri (una stringa vuota li azzera). `defaultSuccessExitCodes` vale `[0, 3010, 1638]` se omesso: 3010 = riavvio richiesto, 1638 = già installato.
- Se manca un file obbligatorio lo step fallisce prima di avviare qualunque processo; con `ignoreIfNotFound: true` logga un avviso e prosegue. Ci si ferma al primo item fallito.
- `manualStepsWarning` è un messaggio mostrato a fine step, come promemoria per i passaggi manuali.
- Con `--dry-run` mostra i comandi senza avviarli; un file non ancora presente è solo un avviso, perché potrebbe copiarlo uno step precedente.

**`MSI-INSTALL`**: installa in sequenza i pacchetti `.msi` di `paths` con `msiexec.exe`.
- Ogni percorso di `paths` viene installato con gli argomenti `msiArguments`, che valgono `/i "{MsiPath}" /qn /norestart` se omessi. `{MsiPath}` non è una variabile: è il percorso dell'elemento di `paths` che si sta installando, e definirlo in `variables` è un errore. `msiArguments` deve contenerlo. `successExitCodes` vale `[0, 3010]`; `ignoreIfNotFound` come sopra.
- File mancanti, fallimenti e dry-run funzionano come in `execute-process`.

Tutti i placeholder `{Nome}` di `variables` si possono usare in entrambe le azioni.

Per elencare le azioni disponibili con una descrizione breve usa `terraform action --list`. Per la configurazione dettagliata di una singola azione usa `terraform action <ActionType> --help`; `terraform action --help` mostra tutte le guide.

## Aggiungere un'azione

Implementare `IAction` (`ActionType`, `ActionDescription`, `Help` e `ExecuteAsync`) e aggiungerla al catalogo `ActionCatalog`.
