# Configuratore HTML delle pipeline

Il progetto genera un catalogo JSON Schema riflesso dalle action in `ActionCatalog` e lo incorpora, insieme al frontend, a Tailwind e alla libreria YAML, in `dist/terraform-configurator.html`. Il file HTML può essere aperto localmente senza rete o servizi runtime.

## Build e test

Da `html-configurator`:

```powershell
npm ci
npm test
npx playwright install chromium
npm run test:browser
```

La build principale di Terraform genera `dist/actions.schema.json` e `dist/terraform-configurator.html`, poi copia l'HTML nella cartella di output e pubblicazione. Per generare lo schema manualmente dopo la compilazione:

```powershell
dotnet run --project SchemaGenerator/SchemaGenerator.csproj -- ..\..\bin\Debug\net9.0\terraform.dll dist\actions.schema.json
npm run build -- dist\actions.schema.json
```

Ogni proprietà di configurazione deve dichiarare `ConfigFieldAttribute`; ogni action dichiara descrizione e tipo config con gli attributi in `Metadata`. La generazione fallisce se i metadati o il tipo .NET non sono supportati. Le annotazioni descrivono il contratto UI e non modificano l'esecuzione delle action.

L'editor conserva il documento YAML e aggiorna soltanto i nodi modificati, mantenendo proprietà sconosciute e commenti. I documenti con errori, avvisi o struttura radice non modificabile sono mostrati in sola lettura. I default sono indicativi e vengono serializzati solo quando scelti esplicitamente. Non esiste salvataggio automatico, telemetria o esecuzione di Terraform nel browser; validare l'export con `terraform validate`.
