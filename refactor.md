# Contesto e Obiettivo
Sei un Senior Software Architect esperto in .NET 9 e C#. Il tuo compito è eseguire il refactoring di un tool CLI di migrazione (attualmente strutturato in modo monolitico con classi `Phase1`...`Phase5`) per trasformarlo in un **Config-Driven Orchestration Engine** generico, modulare e ad altissime prestazioni.

Il motore dovrà leggere una pipeline da un file JSON, risolvere dinamicamente variabili di ambiente/configurazione ed eseguire azioni isolate.

**IMPORTANTE - LIBRERIA ESTERNA:** 
Per le operazioni di I/O ad alte prestazioni e il filtraggio dei file, devi fare riferimento e integrare il codice presente localmente in:
`C:\Users\u1617\Desktop\Projects\C#\cscli\swiss\lib`
(In particolare usa `FileFilterFactory`, e le logiche di `PatternMatchType` in questo modo ovunque ci sarà da fare filtri sui file sarà semplicissimo grazie alla FileFilterFactory semplice e super modulare, il filtro si adatta benissimo all'uso del `FileSystemEnumerable`, evita GetFiles preferisci un lavoro in stream, buffered).

---

## 1. Principi Architetturali

1. **Interfaccia `IAction`**: Rimuovere le vecchie classi `Phase1`..`Phase5`. Ogni step del JSON viene eseguito da un'istanza che implementa `IAction`.
2. **Generic File Batch Action (`file-batch-process`)**: Per le operazioni sui file (es. registrazione DLL, copie condizionali), usa la libreria locale in `swiss\lib` per cercare i file su cui eseguire poi le operazioni in parallelo ed eseguire processi CLI mirati.
3. **`VariableEngine`**: Un motore per sostituire token (es. `{SourceServer}`, `{TargetIP}`, `{FilePath}`) nei payload JSON prima di passargli all'azione.
4. **Dependency Injection**: Le azioni devono essere registrate e risolte dinamicamente tramite un `ActionResolver`.

---

## 2. Contratti Base (da implementare per primi)

Crea i seguenti file in `Lib/Engine/`:

**`IAction.cs`**
```csharp
namespace terraform.Lib.Engine;

public record ActionResult(bool Success, string Message = "");

public class ActionContext
{
    public required bool IsDryRun { get; init; }
    public required terraform.lib.logger.Logger Logger { get; init; }
    public required VariableEngine Variables { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public interface IAction
{
    string ActionType { get; }
    Task<ActionResult> ExecuteAsync(ActionContext context, System.Text.Json.JsonElement config);
}
```

---

## 3. Struttura del nuovo `pipeline.json`

Il motore dovrà parsare questo formato:

```json
{
  "variables": {
    "SourceServer": "TEBTCH04",
    "TargetIP": "10.20.51.200",
    "RootDir": "D:\\gruppotesi"
  },
  "pipeline": [
    {
      "id": "step_01_folders",
      "name": "Crea Struttura Cartelle",
      "type": "ensure-directory",
      "enabled": true,
      "config": {
        "paths": ["D:\\elaborazioni", "{RootDir}\\dll"]
      }
    },
    {
      "id": "step_02_reg_dll",
      "name": "Registrazione DLL COM",
      "type": "file-batch-process",
      "enabled": true,
      "config": {
        "searchPaths": ["{RootDir}\\dll"],
        "recurse": true,
        "maxConcurrency": 16,
        "filter": {
          "pattern": "^EUR[CW].*\\.dll$",
          "matchType": "Regex",
          "ignoreCase": true
        },
        "command": "regsvr32.exe",
        "arguments": "/s \"{FilePath}\""
      }
    }
  ]
}
```

---

## 4. Azioni Concrete da Implementare

Dopo aver creato i contratti, implementa queste classi `IAction`:

1. **`EnsureDirectoryAction` (`type: "ensure-directory"`)**: Crea le directory passate in `config.paths`.
2. **`FileBatchProcessAction` (`type: "file-batch-process"`)**:
   - Usa `FileFilterFactory.CreateFilter` passandogli il `config.filter`.
   - Usa `FastWalker.Walk` per trovare i file in `config.searchPaths`.
   - Per ogni file trovato, usa `Parallel.ForEachAsync` per eseguire `config.command` iniettando `{FilePath}`, `{FileName}` o `{Directory}` negli arguments.
3. **`SqlExecuteAction` (`type: "sql-execute"`)**: Esecuzione script/query sfruttando la classe `DbManager` esistente.
4. **`EditHostsAction` (`type: "edit-hosts"`)**: Patching del file hosts locale.
5. **`RobocopyAction` (`type: "robocopy"`)**: Wrapper per `robocopy.exe`.

---

## 5. Piano di Esecuzione (Per Copilot)

Procedi in quest'ordine, mostrando il codice passo dopo passo per evitare regressioni:
1. Genera `VariableEngine.cs` (sostituzione token con Regex).
2. Genera `ActionContext` e `IAction`.
3. Implementa `PipelineRunner` (legge il JSON, risolve le variabili, itera la pipeline ed esegue le azioni `enabled: true`).
4. Implementa `FileBatchProcessAction` interfacciandoti con i file in `C:\Users\u1617\Desktop\Projects\C#\cscli\swiss\lib`.