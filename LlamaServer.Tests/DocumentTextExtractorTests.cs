using LlamaServer.Models;
using LlamaServer.Services;
using Xunit;

namespace LlamaServer.Tests;

public sealed class DocumentTextExtractorTests
{
    [Fact]
    public async Task ExtractsUtf8Text()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llama-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "olá RAG");
        try
        {
            var sections = await new DocumentTextExtractor().ExtractAsync(
                new DocumentFile { Extension = ".txt", StoredPath = path }, CancellationToken.None);
            Assert.Equal("olá RAG", sections.Single().Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NormalizesJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"llama-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{\"answer\":42}");
        try
        {
            var sections = await new DocumentTextExtractor().ExtractAsync(
                new DocumentFile { Extension = ".json", StoredPath = path }, CancellationToken.None);
            Assert.Contains("\"answer\": 42", sections.Single().Text);
        }
        finally { File.Delete(path); }
    }
}
