using Microsoft.EntityFrameworkCore;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class DocumentIndexWorker(
    DocumentIndexQueue queue,
    IServiceScopeFactory scopes,
    ILogger<DocumentIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeuePendingAsync(stoppingToken);
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider
                    .GetRequiredService<DocumentIndexer>()
                    .IndexAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Erro ao indexar o documento {DocumentId}", id);
            }
        }
    }

    private async Task RequeuePendingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var documents = await db.DocumentFiles
            .Where(document => document.Status == DocumentStatus.Pending || document.Status == DocumentStatus.Processing)
            .ToListAsync(cancellationToken);
        foreach (var document in documents)
        {
            document.Status = DocumentStatus.Pending;
            document.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var document in documents)
        {
            await queue.EnqueueAsync(document.Id, cancellationToken);
        }
    }
}
