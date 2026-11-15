using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class LlamaCppClient(
    HttpClient httpClient,
    SettingsManager settings,
    LlamaSharpClient llamaSharp)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var serverSettings = await settings.GetAsync(cancellationToken);
        if (serverSettings.Engine == "llamasharp")
        {
            return await llamaSharp.GetStatusAsync(cancellationToken);
        }

        var engineUrl = serverSettings.EngineUrl.TrimEnd('/');

        try
        {
            using var response = await httpClient.GetAsync($"{engineUrl}/v1/models", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new EngineStatus(true, engineUrl, await GetCurrentModelAsync(cancellationToken), null);
            }

            return new EngineStatus(false, engineUrl, null, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new EngineStatus(false, engineUrl, null, exception.Message);
        }
    }

    public async Task<string> CompleteChatAsync(
        IReadOnlyList<LlamaChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var serverSettings = await settings.GetAsync(cancellationToken);
        if (serverSettings.Engine == "llamasharp")
        {
            return await llamaSharp.CompleteChatAsync(messages, cancellationToken);
        }

        var engineUrl = serverSettings.EngineUrl.TrimEnd('/');
        var response = await httpClient.PostAsJsonAsync(
            $"{engineUrl}/v1/chat/completions",
            new
            {
                model = await GetCurrentModelAsync(cancellationToken),
                messages,
                stream = false,
                temperature = 0.7
            },
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<LlamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var serverSettings = await settings.GetAsync(cancellationToken);
        if (serverSettings.Engine == "llamasharp")
        {
            await foreach (var token in llamaSharp.StreamChatAsync(messages, cancellationToken))
            {
                yield return token;
            }

            yield break;
        }

        var engineUrl = serverSettings.EngineUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{engineUrl}/v1/chat/completions");

        request.Content = JsonContent.Create(
            new
            {
                model = await GetCurrentModelAsync(cancellationToken),
                messages,
                stream = true,
                temperature = 0.7
            },
            options: JsonOptions);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data: ".Length..];
            if (payload == "[DONE]")
            {
                yield break;
            }

            using var document = JsonDocument.Parse(payload);
            var delta = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("delta");

            if (delta.TryGetProperty("content", out var content))
            {
                yield return content.GetString() ?? "";
            }
        }
    }

    private async Task<string?> GetCurrentModelAsync(CancellationToken cancellationToken)
    {
        var engineUrl = (await settings.GetAsync(cancellationToken)).EngineUrl.TrimEnd('/');
        using var response = await httpClient.GetAsync($"{engineUrl}/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var data = document.RootElement.GetProperty("data");
        return data.GetArrayLength() == 0
            ? null
            : data[0].GetProperty("id").GetString();
    }
}
