using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using LlamaServer.Data;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class DocumentIndexer(
    AppDbContext db,
    SettingsManager settings,
    DocumentTextExtractor extractor,
    EmbeddingModelCache embeddings)
{
    public async Task IndexAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await db.DocumentFiles
            .Include(candidate => candidate.Chunks)
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (document is null)
        {
            return;
        }

        document.Status = DocumentStatus.Processing;
        document.ErrorMessage = null;
        db.DocumentChunks.RemoveRange(document.Chunks);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var config = await settings.GetDocumentSettingsAsync(cancellationToken);
            var sections = await extractor.ExtractAsync(document, cancellationToken);
            var chunks = Chunk(sections, config.ChunkSize, config.ChunkOverlap).ToList();
            if (chunks.Count == 0)
            {
                throw new InvalidOperationException("Não foi encontrado texto extraível no ficheiro.");
            }

            foreach (var chunk in chunks)
            {
                var vector = await embeddings.GetEmbeddingAsync(chunk.Text, cancellationToken);
                db.DocumentChunks.Add(new DocumentChunk
                {
                    DocumentFileId = document.Id,
                    ChunkIndex = chunk.Index,
                    Text = chunk.Text,
                    Embedding = ToBytes(vector),
                    PageNumber = chunk.PageNumber,
                    Section = chunk.Section
                });
            }

            document.Status = DocumentStatus.Ready;
            document.EmbeddingModel = config.EmbeddingModel;
            document.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            document.Status = DocumentStatus.Failed;
            document.ErrorMessage = exception.Message.Length > 2000
                ? exception.Message[..2000]
                : exception.Message;
            document.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public static byte[] ToBytes(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[index]));
        }

        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            return [];
        }

        var values = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float))));
        }

        return values;
    }

    private static IEnumerable<ChunkValue> Chunk(
        IEnumerable<ExtractedSection> sections,
        int tokenSize,
        int tokenOverlap)
    {
        var chunkSize = Math.Max(800, tokenSize * 4);
        var overlap = Math.Min(chunkSize - 1, tokenOverlap * 4);
        var index = 0;
        foreach (var section in sections)
        {
            var text = section.Text.Trim();
            for (var start = 0; start < text.Length; start += chunkSize - overlap)
            {
                var length = Math.Min(chunkSize, text.Length - start);
                var value = text.Substring(start, length).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return new ChunkValue(index++, value, section.PageNumber, section.Section);
                }

                if (start + length >= text.Length)
                {
                    break;
                }
            }
        }
    }

    private sealed record ChunkValue(int Index, string Text, int? PageNumber, string? Section);
}
