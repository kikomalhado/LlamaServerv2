using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class SettingsManager(
    AppDbContext db,
    IOptions<LlamaServerOptions> options)
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
}
