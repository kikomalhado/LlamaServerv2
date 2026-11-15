using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class ModelManager(
    AppDbContext db,
    SettingsManager settings,
    IWebHostEnvironment environment)
{
    private const string ActiveModelKey = "ActiveModel";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var active = await GetActiveModelAsync(cancellationToken);
        var directory = await EnsureModelDirectoryAsync(cancellationToken);

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
        var directory = await EnsureModelDirectoryAsync(cancellationToken);
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

        var serverSettings = await settings.GetAsync(cancellationToken);
        return new LoadModelResponse(
            fileName,
            path,
            serverSettings.EngineUrl,
            "Modelo ativo guardado no gestor. Reinicia o llama.cpp server se o motor estiver a correr com outro modelo.");
    }

    private async Task<string?> GetActiveModelAsync(CancellationToken cancellationToken) =>
        await db.Settings
            .Where(setting => setting.Key == ActiveModelKey)
            .Select(setting => setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string> EnsureModelDirectoryAsync(CancellationToken cancellationToken)
    {
        var serverSettings = await settings.GetAsync(cancellationToken);
        var directory = Path.IsPathRooted(serverSettings.ModelDirectory)
            ? serverSettings.ModelDirectory
            : Path.Combine(environment.ContentRootPath, serverSettings.ModelDirectory);

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
