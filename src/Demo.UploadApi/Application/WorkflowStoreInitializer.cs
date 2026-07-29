using Demo.UploadApi.Persistence;

namespace Demo.UploadApi.Application;

public sealed class WorkflowStoreInitializer(IWorkflowStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
