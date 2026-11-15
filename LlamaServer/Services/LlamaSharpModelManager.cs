using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class LlamaSharpModelManager(
    SettingsManager settings,
    IWebHostEnvironment environment)
{
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var config = await settings.GetLlamaSharpAsync(cancellationToken);
        return GetDirectories(config.ModelDirectory)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var file = new FileInfo(path);
                return new ModelInfo(file.Name, file.FullName, file.Length, file.Name == config.Model);
            })
            .OrderBy(model => model.Name)
            .ToList();
    }

    public async Task<bool> SelectModelAsync(string model, CancellationToken cancellationToken)
    {
        var selected = Path.GetFileName(model);
        if (string.IsNullOrWhiteSpace(selected)
            || !(await ListModelsAsync(cancellationToken)).Any(candidate => candidate.Name == selected))
        {
            return false;
        }

        await settings.SetLlamaSharpModelAsync(selected, cancellationToken);
        return true;
    }

    public string ResolveModelPath(LlamaSharpConfig config)
    {
        var model = Path.GetFileName(config.Model);
        return GetDirectories(config.ModelDirectory)
            .Select(directory => Path.Combine(directory, model))
            .FirstOrDefault(File.Exists) ?? Path.Combine(GetDirectories(config.ModelDirectory).First(), model);
    }

    private IReadOnlyList<string> GetDirectories(string configuredDirectories) => configuredDirectories
        .Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .DefaultIfEmpty("LLamaSharp models")
        .Select(ResolveDirectory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private string ResolveDirectory(string configuredDirectory)
    {
        var directory = Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(environment.ContentRootPath, configuredDirectory);

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
