using Spectre.Console.Cli;
using terraform.Migrator;

// ==============================================================================
// terraform Console Application - Multi-command CLI
// ==============================================================================

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("edi");

    // Comando Migrator
    config.AddCommand<MigratorCommand>("migrator")
        .WithDescription("Migrazione server terraform - clona configurazioni da server sorgente")
        .WithExample("migrator", "--source-server", "TEBTCH04", "--source-computer", "VM1-3LW11N6-29")
        .WithExample("migrator", "-s", "TEBTCH04", "-c", "VM1-3LW11N6-29", "--start-from", "RegisterDll")
        .WithExample("migrator", "-s", "TEBTCH04", "-c", "VM1-3LW11N6-29", "--dry-run");
});

return await app.RunAsync(args);

// dotnet publish -c Release -r win-x64// dotnet publish -c Release -r win-x64