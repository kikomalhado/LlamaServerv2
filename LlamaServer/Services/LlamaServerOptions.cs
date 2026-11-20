namespace LlamaServer.Services;

public sealed class LlamaServerOptions
{
    public const string SectionName = "LlamaServer";

    public string Engine { get; set; } = "llamasharp";

    public string EngineUrl { get; set; } = "http://127.0.0.1:8080";

    public string ModelDirectory { get; set; } = "LocalModels";

    public string LlamaSharpModelDirectory { get; set; } = "LLamaSharp models";

    public uint LlamaSharpContextSize { get; set; } = 2048;

    public int LlamaSharpGpuLayerCount { get; set; } = -1;

    public int LlamaSharpThreads { get; set; }

    public uint LlamaSharpBatchSize { get; set; } = 512;

    public string DocumentsRoot { get; set; } = "AppData/Documents";

    public long DocumentMaxFileSizeBytes { get; set; } = 104_857_600;

    public long DocumentUserQuotaBytes { get; set; } = 1_073_741_824;

    public long DocumentGlobalQuotaBytes { get; set; } = 10_737_418_240;

    public string EmbeddingModelDirectory { get; set; } = "Embedding models";

    public string EmbeddingModel { get; set; } = "";

    public int DocumentIndexingConcurrency { get; set; } = 1;

    public int RagChunkSize { get; set; } = 800;

    public int RagChunkOverlap { get; set; } = 120;

    public int RagResultCount { get; set; } = 6;
}
