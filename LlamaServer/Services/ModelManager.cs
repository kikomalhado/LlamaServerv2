using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class ModelManager(
    AppDbContext db,
    IOptions<LlamaServerOptions> options,
    IWebHostEnvironment environment)
{
    private const string ActiveModelKey = "ActiveModel";
    private readonly LlamaServerOptions _options = options.Value;

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var active = await GetActiveModelAsync(cancellationToken);
        var directory = EnsureModelDirectory();

        return Directory
            .EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var file = new FileInfo(path);
                return new ModelInfo(file.Name, file.FullName, file.Length, file.Name == active);
            })
            .OrderBy(model => model.Name)
            .ToList();
    }

    public async Task<LoadModelResponse?> LoadModelAsync(string model, CancellationToken cancellationToken)
    {
        var directory = EnsureModelDirectory();
        var fileName = Path.GetFileName(model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{model}.gguf");
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            return null;
        }

        var setting = await db.Settings.FindAsync([ActiveModelKey], cancellationToken);
        if (setting is null)
        {
            setting = new AppSetting { Key = ActiveModelKey };
            db.Settings.Add(setting);
        }

        setting.Value = fileName;
        await db.SaveChangesAsync(cancellationToken);

        return new LoadModelResponse(
            fileName,
            path,
            _options.EngineUrl,
            "Modelo ativo guardado no gestor. Se o llama.cpp server ja estiver a correr com outro modelo, reinicia o motor apontando para este ficheiro.");
    }

    private async Task<string?> GetActiveModelAsync(CancellationToken cancellationToken) =>
        await db.Settings
            .Where(setting => setting.Key == ActiveModelKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

    private string EnsureModelDirectory()
    {
        var directory = Path.IsPathRooted(_options.ModelDirectory)
            ? _options.ModelDirectory
            : Path.Combine(environment.ContentRootPath, _options.ModelDirectory);

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
