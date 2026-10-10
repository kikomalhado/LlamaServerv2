namespace LlamaServer.Services;

public static class Sse
{
    public static string Encode(string value) =>
        value.ReplaceLineEndings("\ndata: ");
}
