using System.Collections.Concurrent;
using LLama;
using LLama.Common;
using LLama.Native;

namespace LlamaServer.Services;

public sealed class EmbeddingModelCache(
    IWebHostEnvironment environment,
    IServiceScopeFactory scopes) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<LLamaWeights>> _models = new(StringComparer.OrdinalIgnoreCase);

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<SettingsManager>()
            .GetDocumentSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.EmbeddingModel))
        {
            throw new InvalidOperationException("O admin ainda não configurou um modelo de embeddings.");
        }

        var directory = Path.IsPathRooted(config.EmbeddingModelDirectory)
            ? config.EmbeddingModelDirectory
            : Path.Combine(environment.ContentRootPath, config.EmbeddingModelDirectory);
        var path = Path.Combine(Path.GetFullPath(directory), Path.GetFileName(config.EmbeddingModel));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("O modelo de embeddings não foi encontrado.");
        }

        var parameters = new ModelParams(path)
        {
            ContextSize = 2048,
            GpuLayerCount = -1,
            Embeddings = true,
            PoolingType = LLamaPoolingType.Mean
        };
        var model = _models.GetOrAdd(
            Path.GetFullPath(path),
            _ => new Lazy<LLamaWeights>(
                () => LLamaWeights.LoadFromFile(parameters),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        using var embedder = new LLamaEmbedder(model, parameters);
        var embeddings = await embedder.GetEmbeddings(text, cancellationToken);
        return embeddings.FirstOrDefault() ?? [];
    }

    public void Dispose()
    {
        foreach (var model in _models.Values.Where(model => model.IsValueCreated))
        {
            model.Value.Dispose();
        }

        _models.Clear();
    }
}
