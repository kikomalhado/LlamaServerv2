using System.Collections.Concurrent;
using LLama;
using LLama.Common;

namespace LlamaServer.Services;

public sealed class LlamaSharpModelCache : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<LLamaWeights>> _models = new(StringComparer.OrdinalIgnoreCase);

    public LLamaWeights GetOrLoad(string path, ModelParams parameters)
    {
        var key = $"{Path.GetFullPath(path)}|gpu:{parameters.GpuLayerCount}";
        var lazy = _models.GetOrAdd(
            key,
            _ => new Lazy<LLamaWeights>(
                () => LLamaWeights.LoadFromFile(parameters),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
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
