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
}
