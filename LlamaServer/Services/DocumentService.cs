using Microsoft.EntityFrameworkCore;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class DocumentService(
    AppDbContext db,
    SettingsManager settings,
    DocumentIndexQueue queue,
    IWebHostEnvironment environment)
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".txt", ".md", ".json"
    };

    public async Task<IReadOnlyList<DocumentInfo>> ListAsync(
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        var documents = await db.DocumentFiles
            .Where(document => isAdmin || document.IsShared || document.OwnerUserId == userId)
            .OrderByDescending(document => document.UpdatedAt)
            .ToListAsync(cancellationToken);
        return documents.Select(ToInfo).ToList();
    }

    public async Task<DocumentInfo> UploadAsync(
        Guid userId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var config = await settings.GetDocumentSettingsAsync(cancellationToken);
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new ArgumentException("Formato não suportado. Usa PDF, DOCX, XLSX, TXT, MD ou JSON.");
        }

        if (file.Length <= 0 || file.Length > config.MaxFileSizeBytes)
        {
            throw new ArgumentException("O ficheiro excede o tamanho máximo permitido.");
        }

        var userBytes = await db.DocumentFiles
            .Where(document => document.OwnerUserId == userId)
            .SumAsync(document => (long?)document.SizeBytes, cancellationToken) ?? 0;
        if (userBytes + file.Length > config.UserQuotaBytes)
        {
            throw new ArgumentException("A quota de documentos do utilizador foi excedida.");
        }

        var globalBytes = await db.DocumentFiles
            .SumAsync(document => (long?)document.SizeBytes, cancellationToken) ?? 0;
        if (globalBytes + file.Length > config.GlobalQuotaBytes)
        {
            throw new ArgumentException("A quota global de documentos foi excedida.");
        }

        var originalName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalName) || originalName.Length > 255)
        {
            throw new ArgumentException("O nome do ficheiro não é válido.");
        }

        var id = Guid.NewGuid();
        var directory = ResolveRoot(config.DocumentsRoot, id);
        Directory.CreateDirectory(directory);
        var storedPath = Path.Combine(directory, $"source{extension}");

        try
        {
            await using var input = file.OpenReadStream();
            await using var output = new FileStream(storedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
            if (output.Length > config.MaxFileSizeBytes)
            {
                throw new ArgumentException("O ficheiro excede o tamanho máximo permitido.");
            }

            var document = new DocumentFile
            {
                Id = id,
                OwnerUserId = userId,
                OriginalName = originalName,
                StoredPath = storedPath,
                Extension = extension,
                SizeBytes = output.Length,
                Status = DocumentStatus.Pending
            };
            db.DocumentFiles.Add(document);
            await db.SaveChangesAsync(cancellationToken);
            await queue.EnqueueAsync(id, cancellationToken);
            return ToInfo(document);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(Guid id, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var document = await db.DocumentFiles.FirstOrDefaultAsync(document => document.Id == id, cancellationToken);
        if (document is null || (!isAdmin && (document.OwnerUserId != userId || document.IsShared)))
        {
            return false;
        }

        db.DocumentFiles.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        TryDeleteDirectory(Path.GetDirectoryName(document.StoredPath));
        return true;
    }

    public async Task<bool> SetSharedAsync(Guid id, bool shared, CancellationToken cancellationToken)
    {
        var document = await db.DocumentFiles.FindAsync([id], cancellationToken);
        if (document is null)
        {
            return false;
        }

        document.IsShared = shared;
        document.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReindexAsync(Guid id, Guid userId, bool isAdmin, CancellationToken cancellationToken)
    {
        var document = await db.DocumentFiles.FirstOrDefaultAsync(document => document.Id == id, cancellationToken);
        if (document is null || (!isAdmin && document.OwnerUserId != userId))
        {
            return false;
        }

        document.Status = DocumentStatus.Pending;
        document.ErrorMessage = null;
        document.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(id, cancellationToken);
        return true;
    }

    private string ResolveRoot(string root, Guid id)
    {
        var resolved = Path.IsPathRooted(root) ? root : Path.Combine(environment.ContentRootPath, root);
        return Path.Combine(Path.GetFullPath(resolved), id.ToString("N"));
    }

    private static DocumentInfo ToInfo(DocumentFile document) => new(
        document.Id,
        document.OriginalName,
        document.Extension,
        document.SizeBytes,
        document.IsShared,
        document.Status,
        document.ErrorMessage,
        document.CreatedAt,
        document.UpdatedAt);

    private static void TryDeleteDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
