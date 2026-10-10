namespace LlamaServer.Services;

public sealed class LlamaServerOptions
{
    public const string SectionName = "LlamaServer";

    public string EngineUrl { get; set; } = "http://127.0.0.1:8080";

    public string ModelDirectory { get; set; } = "LocalModels";
}
