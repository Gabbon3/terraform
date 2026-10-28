using terraform.lib.engine.actions;
using terraform.lib.utils;

namespace terraform.lib.engine;

public static class ActionCatalog
{
    public static IReadOnlyList<IAction> Create(IProcessRunner processRunner) =>
    [
        new EnsureDirectoryAction(),
        new FileBatchProcessAction(processRunner),
        new EditHostsAction(),
        new RobocopyAction(processRunner),
        new ExecuteProcessAction(processRunner),
        new MsiInstallAction(processRunner),
        new CreateServicesAction(processRunner),
        new CreateTasksAction(processRunner)
    ];
}
