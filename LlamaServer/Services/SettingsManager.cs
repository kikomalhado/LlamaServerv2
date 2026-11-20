using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class SettingsManager(
    AppDbContext db,
    IOptions<LlamaServerOptions> options,
    DocumentIndexQueue queue)
{
    private const string EngineUrlKey = "EngineUrl";
    private const string ModelDirectoryKey = "ModelDirectory";
    private const string EngineKey = "Engine";
    private const string LlamaSharpModelDirectoryKey = "LlamaSharpModelDirectory";
    private const string LlamaSharpModelKey = "LlamaSharpModel";
    private const string LlamaSharpContextSizeKey = "LlamaSharpContextSize";
    private const string LlamaSharpGpuLayerCountKey = "LlamaSharpGpuLayerCount";
    private const string LlamaSharpThreadsKey = "LlamaSharpThreads";
    private const string LlamaSharpBatchSizeKey = "LlamaSharpBatchSize";
    private const string LlamaSharpAllowedModelsKey = "LlamaSharpAllowedModels";
    private const string DocumentsRootKey = "DocumentsRoot";
    private const string DocumentMaxFileSizeKey = "DocumentMaxFileSizeBytes";
    private const string DocumentUserQuotaKey = "DocumentUserQuotaBytes";
    private const string DocumentGlobalQuotaKey = "DocumentGlobalQuotaBytes";
    private const string EmbeddingModelDirectoryKey = "EmbeddingModelDirectory";
    private const string EmbeddingModelKey = "EmbeddingModel";
    private const string DocumentIndexingConcurrencyKey = "DocumentIndexingConcurrency";
    private const string RagChunkSizeKey = "RagChunkSize";
    private const string RagChunkOverlapKey = "RagChunkOverlap";
    private const string RagResultCountKey = "RagResultCount";
    private readonly LlamaServerOptions _defaults = options.Value;

    public async Task<ServerSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var values = await db.Settings
            .Where(setting => setting.Key == EngineKey || setting.Key == EngineUrlKey || setting.Key == ModelDirectoryKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        return new ServerSettingsDto(
            GetValue(values, EngineKey, _defaults.Engine),
            GetValue(values, EngineUrlKey, _defaults.EngineUrl),
            GetValue(values, ModelDirectoryKey, _defaults.ModelDirectory));
    }

    public async Task<ServerSettingsDto> UpdateAsync(
        UpdateServerSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var engine = request.Engine.Trim().ToLowerInvariant();
        var engineUrl = request.EngineUrl.Trim().TrimEnd('/');
        var modelDirectory = request.ModelDirectory.Trim();

        if (engine is not ("llama.cpp" or "llamasharp"))
        {
            throw new ArgumentException("Engine inválida. Usa 'llama.cpp' ou 'llamasharp'.", nameof(request));
        }

        if (!Uri.TryCreate(engineUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("O endereço do motor tem de ser um URL HTTP ou HTTPS.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(modelDirectory)
            || Path.GetFileName(modelDirectory) is "." or "..")
        {
            throw new ArgumentException("A pasta de modelos é obrigatória.", nameof(request));
        }

        await SetAsync(EngineKey, engine, cancellationToken);
        await SetAsync(EngineUrlKey, engineUrl, cancellationToken);
        await SetAsync(ModelDirectoryKey, modelDirectory, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return new ServerSettingsDto(engine, engineUrl, modelDirectory);
    }

    public async Task<LlamaSharpConfig> GetLlamaSharpAsync(CancellationToken cancellationToken)
    {
        var values = await db.Settings
            .Where(setting => setting.Key == LlamaSharpModelDirectoryKey
                || setting.Key == LlamaSharpModelKey
                || setting.Key == LlamaSharpContextSizeKey
                || setting.Key == LlamaSharpGpuLayerCountKey
                || setting.Key == LlamaSharpThreadsKey
                || setting.Key == LlamaSharpBatchSizeKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        return new LlamaSharpConfig(
            GetValue(values, LlamaSharpModelDirectoryKey, _defaults.LlamaSharpModelDirectory),
            GetValue(values, LlamaSharpModelKey, ""),
            GetUIntValue(values, LlamaSharpContextSizeKey, _defaults.LlamaSharpContextSize),
            GetIntValue(values, LlamaSharpGpuLayerCountKey, _defaults.LlamaSharpGpuLayerCount),
            GetIntValue(values, LlamaSharpThreadsKey, _defaults.LlamaSharpThreads),
            GetUIntValue(values, LlamaSharpBatchSizeKey, _defaults.LlamaSharpBatchSize));
    }

    public async Task<LlamaSharpConfig> UpdateLlamaSharpAsync(
        UpdateLlamaSharpSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var modelDirectory = request.ModelDirectory.Trim();
        if (string.IsNullOrWhiteSpace(modelDirectory)
            || modelDirectory.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(directory => Path.GetFileName(directory) is "." or ".."))
        {
            throw new ArgumentException("A pasta de modelos LLamaSharp é obrigatória.", nameof(request));
        }

        if (request.ContextSize is < 256 or > 131072)
        {
            throw new ArgumentException("O contexto deve estar entre 256 e 131072.", nameof(request));
        }

        if (request.GpuLayerCount < -1)
        {
            throw new ArgumentException("As camadas GPU devem ser -1 (automático), 0 (CPU) ou positivas.", nameof(request));
        }

        if (request.Threads < 0 || request.BatchSize is < 32 or > 8192)
        {
            throw new ArgumentException("Threads não podem ser negativos e o batch deve estar entre 32 e 8192.", nameof(request));
        }

        await SetAsync(LlamaSharpModelDirectoryKey, modelDirectory, cancellationToken);
        await SetAsync(LlamaSharpContextSizeKey, request.ContextSize.ToString(), cancellationToken);
        await SetAsync(LlamaSharpGpuLayerCountKey, request.GpuLayerCount.ToString(), cancellationToken);
        await SetAsync(LlamaSharpThreadsKey, request.Threads.ToString(), cancellationToken);
        await SetAsync(LlamaSharpBatchSizeKey, request.BatchSize.ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetLlamaSharpAsync(cancellationToken);
    }

    public async Task SetLlamaSharpModelAsync(string model, CancellationToken cancellationToken)
    {
        await SetAsync(LlamaSharpModelKey, model, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>?> GetLlamaSharpAllowedModelsAsync(CancellationToken cancellationToken)
    {
        var value = await db.Settings
            .Where(setting => setting.Key == LlamaSharpAllowedModelsKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return value is null
            ? null
            : new HashSet<string>(JsonSerializer.Deserialize<string[]>(value) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetLlamaSharpAllowedModelsAsync(
        IEnumerable<string> models,
        CancellationToken cancellationToken)
    {
        var allowed = models
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(model => model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await SetAsync(LlamaSharpAllowedModelsKey, JsonSerializer.Serialize(allowed), cancellationToken);
        var active = await db.Settings.FindAsync([LlamaSharpModelKey], cancellationToken);
        if (active is not null && !allowed.Contains(active.Value, StringComparer.OrdinalIgnoreCase))
        {
            active.Value = allowed.FirstOrDefault() ?? "";
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentSettingsDto> GetDocumentSettingsAsync(CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            DocumentsRootKey, DocumentMaxFileSizeKey, DocumentUserQuotaKey, DocumentGlobalQuotaKey,
            EmbeddingModelDirectoryKey, EmbeddingModelKey, DocumentIndexingConcurrencyKey,
            RagChunkSizeKey, RagChunkOverlapKey, RagResultCountKey
        };
        var values = await db.Settings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        return new DocumentSettingsDto(
            GetValue(values, DocumentsRootKey, _defaults.DocumentsRoot),
            GetLongValue(values, DocumentMaxFileSizeKey, _defaults.DocumentMaxFileSizeBytes),
            GetLongValue(values, DocumentUserQuotaKey, _defaults.DocumentUserQuotaBytes),
            GetLongValue(values, DocumentGlobalQuotaKey, _defaults.DocumentGlobalQuotaBytes),
            GetValue(values, EmbeddingModelDirectoryKey, _defaults.EmbeddingModelDirectory),
            GetValue(values, EmbeddingModelKey, _defaults.EmbeddingModel),
            GetIntValue(values, DocumentIndexingConcurrencyKey, _defaults.DocumentIndexingConcurrency),
            GetIntValue(values, RagChunkSizeKey, _defaults.RagChunkSize),
            GetIntValue(values, RagChunkOverlapKey, _defaults.RagChunkOverlap),
            GetIntValue(values, RagResultCountKey, _defaults.RagResultCount));
    }

    public async Task<DocumentSettingsDto> UpdateDocumentSettingsAsync(
        UpdateDocumentSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var root = request.DocumentsRoot.Trim();
        var embeddingDirectory = request.EmbeddingModelDirectory.Trim();
        var embeddingModel = Path.GetFileName(request.EmbeddingModel.Trim());
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(embeddingDirectory))
        {
            throw new ArgumentException("As pastas de documentos e embeddings são obrigatórias.", nameof(request));
        }

        if (request.MaxFileSizeBytes < 1_048_576
            || request.UserQuotaBytes < request.MaxFileSizeBytes
            || request.GlobalQuotaBytes < request.UserQuotaBytes)
        {
            throw new ArgumentException("Os limites de ficheiros e quotas não são válidos.", nameof(request));
        }

        if (request.IndexingConcurrency is < 1 or > 4
            || request.ChunkSize is < 200 or > 4000
            || request.ChunkOverlap < 0
            || request.ChunkOverlap >= request.ChunkSize
            || request.ResultCount is < 1 or > 20)
        {
            throw new ArgumentException("Os parâmetros de indexação não são válidos.", nameof(request));
        }

        var oldEmbeddingModel = (await GetDocumentSettingsAsync(cancellationToken)).EmbeddingModel;
        await SetAsync(DocumentsRootKey, root, cancellationToken);
        await SetAsync(DocumentMaxFileSizeKey, request.MaxFileSizeBytes.ToString(), cancellationToken);
        await SetAsync(DocumentUserQuotaKey, request.UserQuotaBytes.ToString(), cancellationToken);
        await SetAsync(DocumentGlobalQuotaKey, request.GlobalQuotaBytes.ToString(), cancellationToken);
        await SetAsync(EmbeddingModelDirectoryKey, embeddingDirectory, cancellationToken);
        await SetAsync(EmbeddingModelKey, embeddingModel, cancellationToken);
        await SetAsync(DocumentIndexingConcurrencyKey, request.IndexingConcurrency.ToString(), cancellationToken);
        await SetAsync(RagChunkSizeKey, request.ChunkSize.ToString(), cancellationToken);
        await SetAsync(RagChunkOverlapKey, request.ChunkOverlap.ToString(), cancellationToken);
        await SetAsync(RagResultCountKey, request.ResultCount.ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (!string.Equals(oldEmbeddingModel, embeddingModel, StringComparison.OrdinalIgnoreCase))
        {
            var documents = await db.DocumentFiles.Where(document => document.Status == DocumentStatus.Ready).ToListAsync(cancellationToken);
            foreach (var document in documents)
            {
                document.Status = DocumentStatus.Pending;
                document.ErrorMessage = null;
                document.UpdatedAt = DateTime.UtcNow;
                await queue.EnqueueAsync(document.Id, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return await GetDocumentSettingsAsync(cancellationToken);
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.Settings.FindAsync([key], cancellationToken);
        if (setting is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
    }

    private static string GetValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static uint GetUIntValue(IReadOnlyDictionary<string, string> values, string key, uint fallback) =>
        values.TryGetValue(key, out var value) && uint.TryParse(value, out var parsed) ? parsed : fallback;

    private static int GetIntValue(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static long GetLongValue(IReadOnlyDictionary<string, string> values, string key, long fallback) =>
        values.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : fallback;
}
