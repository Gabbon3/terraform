# Terraform

CLI leggero e config-driven per orchestrare pipeline di provisioning e configurazione su Windows. Ogni pipeline definisce variabili e step componibili, eseguiti in sequenza dal motore Terraform.

## Prerequisiti

- Windows
- .NET 9 SDK
- Permessi e accesso alle risorse richiesti dalle singole Action (file, share, servizi, task o processi)

Verifica l'SDK con `dotnet --version`.

## Build ed esecuzione

Dalla root del repository:

```powershell
dotnet build
```

Copia `Pipeline/pipeline.sample.yaml` in un file di lavoro, poi valida la configurazione e verifica il piano prima dell'esecuzione:

```powershell
dotnet run -- validate pipeline.yaml
dotnet run -- run pipeline.yaml --dry-run
dotnet run -- run pipeline.yaml
```

Per pubblicare l'applicazione:

```powershell
dotnet publish -c Release -r win-x64
```
# Pipeline engine

Terraform esegue pipeline descritte in file YAML, step dopo step, con azioni generiche e configurabili. Sono accettate le estensioni `.yaml` e `.yml`; i file `.json` non sono piu' supportati.

```powershell
terraform validate pipeline.yaml      # solo configurazione, senza azioni ne' query al server
terraform run pipeline.yaml --dry-run   # valida e mostra cosa verrebbe fatto, senza modificare nulla
terraform run pipeline.yaml
```

Un esempio funzionante è in `Pipeline/pipeline.sample.yaml`. YAML ammette commenti con `#`; non servono virgole finali.

## Struttura

- `variables`: coppie nome/valore, usabili come `{Nome}` (maiuscole e minuscole contano) in qualunque valore stringa delle config. I valori non possono contenere altri token.
- `pipeline`: elenco di step con `id` (univoco), `name` (opzionale), `type`, `enabled` (default `true`) e `config`.
- Prima di eseguire qualsiasi step il motore controlla tutta la pipeline: id, tipi, token e config delle azioni (campi obbligatori, tipi, opzioni, filtri e guardrail). Raccoglie gli errori dei vari step indicando id, config e campo: una config errata in fondo impedisce di avviare anche il primo step. Gli step con `enabled: false` mantengono i controlli strutturali su id/type, ma non vengono validati per tipo risolto, token o config.
- `validate` esegue lo stesso preflight statico di `run` e `run --dry-run`, restituisce `0` se valido e `1` se errato, e non crea log di esecuzione. Non controlla esistenza/accessibilita' di file, credenziali reali o server remoti: questi controlli restano runtime, per permettere agli step precedenti di creare gli artefatti necessari. Per Robocopy anche il riconoscimento sorgente file/cartella resta runtime.
- Gli step girano in ordine; al primo fallimento la pipeline si ferma. I Warning restano non bloccanti e lo stato rimane `Succeeded`: completamento e riepilogo mostrano il numero di avvisi di ogni step, distinguendolo da un risultato pulito.
- Tutti i percorsi devono essere completi (`D:\dir` o `\\server\share`); un percorso relativo è un errore.
- Exit code: `0` successo, `1` errore o pipeline non valida, `130` annullata con Ctrl+C. I log sono in `logs\pipeline_<data>.log` accanto all'eseguibile.

## Azioni

**`REQUIRED-FOLDERS`**: crea le cartelle di `paths` (anche i genitori). Idempotente; fallisce se esiste un file con lo stesso nome.
- Ogni voce di `paths` puo' essere un percorso stringa oppure un oggetto con `path` (destinazione), `source` e `depth`.
- `depth: 0` (default): crea solo `path`, senza leggere la sorgente. `depth: 1`: crea anche le sottocartelle immediate di `source`; valori maggiori includono altrettanti livelli. `depth: -1`: replica tutto l'albero delle cartelle. Altri valori negativi non sono ammessi.
- Per `depth` diverso da 0, `source` e' obbligatorio e deve essere un percorso completo. Non si copiano file, non si cancellano cartelle esistenti e non si seguono junction o link simbolici. La sorgente deve essere accessibile a runtime, anche in dry-run; `validate` non la interroga. Tutti i percorsi da creare sono raccolti e controllati prima della prima creazione.

```yaml
type: REQUIRED-FOLDERS
config:
  paths:
    - 'D:\elaborazioni'
    - path: 'D:\Gruppotesi\dll_euritmo\Logs'
      source: '\\{SourceServer}\D$\Gruppotesi\dll_euritmo\Logs'
      depth: 1
```

**`FILE-BATCH-PROCESS`**: esegue un comando per ogni file trovato.
- `searchPaths` (obbligatorio), `recurse` (default `false`), `maxConcurrency` (default: numero di CPU), `command`, `arguments`.
- `filter` (opzionale): `pattern`, `matchType` (`Auto`, `Regex`, `Fixed`, `Glob`), `ignoreCase` (default `true`), `matchFullPath`, `dateAfter`, `dateBefore` (es. `30d`, `12h`, `2024-01-15`, `60d:c`), `minSize`, `maxSize` (es. `10k`, `5m`).
- `matchType: Auto` non riconosce le regex: un pattern con `*` o `?` diventa Glob, altrimenti è una sottostringa. Per le regex usare `Regex` (niente lookaround né backreference).
- In `arguments` si possono usare `{FilePath}`, `{FileName}` (con estensione) e `{Directory}`; altrove sono un errore. Il comando parte senza shell: i nomi file non vengono interpretati.
- Include file nascosti e di sistema, non segue le junction. `searchPaths` duplicati o annidati con `recurse` attivo sono rifiutati.
- File non leggibili e cartelle con accesso negato vengono segnalati con Warning e percorso, poi saltati: continuano gli altri file, le cartelle sorelle e gli step successivi. Non vengono soppressi gli errori I/O diversi dall'accesso negato.
- Tutti i file vengono elaborati; lo step fallisce se almeno un comando termina con exit code diverso da 0.

**`HOSTS`**: `entries` (`ip`, `host`) e `path` opzionale (default: hosts di sistema). Ogni host resta su una sola riga attiva con l'IP indicato; gli altri alias, i commenti e la codifica del file si conservano. Scrive solo se qualcosa cambia; serve l'amministratore.

**`ROBOCOPY`**: `source`, `destination`, `files`, `excludeDirs`, `excludeFiles`, `mirror`, `threads`, `retries` (1), `waitSeconds` (1), `copyOptions`, `extraArguments`.
- Per una cartella i default sono `/MIR /MT:16 /COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP`, più `/SECFIX` se la destinazione esiste già. Quindi `mirror` vale `true` se omesso e **elimina dalla destinazione ciò che non è nella sorgente**: `mirror: false` toglie `/MIR`.
- Se `source` è un file, `destination` è il percorso completo del file copiato e si usano solo `/COPYALL /B /DCOPY:DAT /R:1 /W:1 /NP` (e `/SECFIX`): niente `/MIR` né `/MT`, e `files`, `excludeDirs`, `excludeFiles` non sono ammessi.
- `copyOptions` sostituisce `/COPYALL /B /DCOPY:DAT` (stringa vuota per nessuna); `extraArguments` si aggiunge in coda senza toccare i default.
- Exit code di robocopy da 0 a 7 sono successo, da 8 in su fallimento.
- Con `--dry-run` aggiunge `/L`: non copia né cancella nulla ma elenca cosa succederebbe.
- Sorgente e destinazione uguali o annidate l'una nell'altra sono rifiutate anche senza mirror, prima di eseguire la pipeline. Il confronto normalizza i percorsi e ignora il case, ma non risolve alias fisici (junction, unita' mappate o share diverse verso la stessa cartella).
- Le operazioni che cancellano (`mirror`, `/MIR`, `/PURGE`, `/MOV`, `/MOVE`) sono rifiutate se la destinazione è la radice di un'unità o di una share: per copiare lì servono `mirror: false` e nessuno di quegli switch. `/MOV` e `/MOVE` sono vietati anche quando la sorgente e' una radice.

**`EXECUTE-PROCESS`**: esegue in sequenza una lista di programmi, tipicamente installer `.exe`.
- `items`: ciascuno con `path` (completo), `name` (etichetta nei log), `arguments`, `successExitCodes` e `ignoreIfNotFound`.
- `defaultArguments` vale per gli item senza `arguments` propri (una stringa vuota li azzera). `defaultSuccessExitCodes` vale `[0, 3010, 1638]` se omesso: 3010 = riavvio richiesto, 1638 = già installato.
- Se manca un file obbligatorio lo step fallisce prima di avviare qualunque processo; con `ignoreIfNotFound: true` logga un avviso e prosegue. Ci si ferma al primo item fallito.
- `manualStepsWarning` è un messaggio mostrato a fine step, come promemoria per i passaggi manuali.
- Con `--dry-run` mostra i comandi senza avviarli; un file non ancora presente è solo un avviso, perché potrebbe copiarlo uno step precedente.

**`MSI-INSTALL`**: installa in sequenza i pacchetti `.msi` di `paths` con `msiexec.exe`.
- Ogni percorso di `paths` viene installato con gli argomenti `msiArguments`, che valgono `/i "{MsiPath}" /qn /norestart` se omessi. `{MsiPath}` non è una variabile: è il percorso dell'elemento di `paths` che si sta installando, e definirlo in `variables` è un errore. `msiArguments` deve contenerlo. `successExitCodes` vale `[0, 3010]`; `ignoreIfNotFound` come sopra.
- File mancanti, fallimenti e dry-run funzionano come in `execute-process`.

**`CREATE-SERVICES`** e **`CREATE-TASKS`**: clonano servizi e task schedulati dal computer remoto al computer locale.
- `sourceComputerName` (obbligatorio): usare ad esempio `"{SourceComputerName}"`, definendo il server nelle `variables` globali.
- `credentials` (obbligatorio): mappa account/password, cercata senza distinzione di maiuscole/minuscole. Le password possono essere riferimenti a variabili globali. Se un account sorgente manca, l'elemento viene saltato con **Warning**, senza sostituire l'utenza con LocalSystem. Una mappa vuota e' ammessa ma fa saltare tutti gli elementi da creare.
- `filter` e i suoi campi sono opzionali. **Senza filtri vengono selezionati tutti i servizi/task**. `accountPattern` applica le wildcard PowerShell all'account sorgente (`StartName` per i servizi, `Principal.UserId` per i task): ad esempio `"*svc_*"` o `"DOMINIO\\svc_*"`.
- `filter.names` e' un array non vuoto di nomi **esatti**, confrontati senza distinzione di case. Per i servizi si usa `Name`, non `DisplayName`; per i task si puo' indicare il nome semplice oppure il percorso completo (`"\\Batch\\Import"`). Un nome task semplice seleziona anche le omonimie in cartelle diverse.
- Se sono presenti sia `accountPattern` sia `names`, si applica l'intersezione dei filtri.
- Il flow segue Phase3: script PowerShell, lettura NDJSON riga per riga e creazione immediata di ogni elemento, senza accumulare l'export o salvarlo su file. Un errore remoto tardivo interrompe lo step ma non annulla gli elementi gia' creati.
- `CREATE-SERVICES` salta i servizi gia' esistenti senza aggiornarli; crea quelli nuovi con startup **Disabled**, conservando nome, display name e comando binario completo (virgolette e argomenti inclusi). Un eseguibile mancante sul target produce Warning ma non impedisce la creazione.
- `CREATE-TASKS` esporta l'XML sorgente, imposta solo `Settings/Enabled=false` (senza alterare i trigger) e registra il task nel percorso originale, **sovrascrivendo quelli esistenti**. XML vuoto: Warning e skip; XML malformato o registrazione fallita: step fallito.
- Nessun servizio/task viene avviato. Primo errore di query o creazione: step fallito e pipeline interrotta; gli elementi gia' creati non vengono annullati.
- Con `--dry-run` si interroga realmente il server e si elaborano gli elementi selezionati senza crearli. Per i servizi si verificano anche esistenza locale, presenza delle credenziali nella mappa e percorso eseguibile; per i task si controllano credenziali e XML e si prepara in memoria la disabilitazione. I log mostrano nome/percorso, utenza, stato Disabled e, per i task, sovrascrittura degli esistenti; il riepilogo distingue trovati, da creare/registrare e saltati. Non vengono creati file XML temporanei ne' eseguiti comandi di scrittura. La presenza di una password nella mappa non ne verifica la validita' presso Windows; la validazione XML non sostituisce quella completa di Task Scheduler.
- Richiedono Windows PowerShell, privilegi amministrativi sul target e accesso remoto con l'identita' che esegue terraform: CIM per i servizi, PowerShell Remoting/WinRM e modulo ScheduledTasks per i task. `credentials` riguarda le utenze degli elementi clonati, non l'autenticazione remota.
- Le password non vengono stampate nei log e non vengono inviate alla query PowerShell; vengono passate a `sc.exe create` o `schtasks.exe /Create`. Gli argomenti dei processi sono ispezionabili da utenti privilegiati: proteggere il file YAML della pipeline e non versionare password reali. L'unico file temporaneo e' l'XML del singolo task richiesto da `schtasks.exe`, rimosso anche su errore o annullamento.

Esempio (sostituire server, account e password prima dell'uso):

```yaml
variables:
  SourceComputerName: SERVER-SORGENTE
  BatchPassword: DA-SOSTITUIRE
pipeline:
  - id: services
    type: CREATE-SERVICES
    config:
      sourceComputerName: '{SourceComputerName}'
      filter:
        accountPattern: '*svc_*'
      credentials:
        'DOMINIO\svc_batch': '{BatchPassword}'
  - id: tasks
    type: CREATE-TASKS
    config:
      sourceComputerName: '{SourceComputerName}'
      filter:
        names: ['\Batch\Import', '\Batch\Export']
      credentials:
        'DOMINIO\svc_batch': '{BatchPassword}'
```

Tutti i placeholder `{Nome}` di `variables` si possono usare nei valori stringa delle config; non nei nomi delle proprieta' (quindi neppure nelle chiavi account di `credentials`).

Per elencare le azioni disponibili con una descrizione breve usa `terraform action --list`. Per la configurazione dettagliata di una singola azione usa `terraform action <ActionType> --help`; `terraform action --help` mostra tutte le guide.

## Aggiungere un'azione

Implementare `IAction` (`ActionType`, `ActionDescription`, `Help`, `Validate` ed `ExecuteAsync`) e aggiungerla al catalogo `ActionCatalog`. `Validate` riceve la config dopo la risoluzione delle variabili globali, deve riusare i parser di esecuzione e non deve fare I/O o modifiche: per errori usare `ActionConfigException` con il nome del campo. I controlli sullo stato del server restano in `ExecuteAsync`.

La logica specifica resta nelle Action; le utility generiche di esecuzione sono in `Lib/Utils`.
Per leggere NDJSON usare `ProcessUtils.ExecuteAndStreamPowerShellAsync(runner, script, cancellationToken)` in un `await foreach`, deserializzando ed elaborando ogni riga nell'azione.
Il runner gestisce streaming senza troncamenti, stderr, exit code e chiusura del processo anche quando il consumer interrompe il ciclo.
Usare `ProcessUtils.PowerShellLiteral` per i valori configurati negli script e `ProcessUtils.QuoteArgument` per gli argomenti di eseguibili Windows avviati senza shell.
