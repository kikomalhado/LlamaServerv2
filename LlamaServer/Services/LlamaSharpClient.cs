using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;
using LlamaServer.Models;

namespace LlamaServer.Services;

public sealed class LlamaSharpClient(
    SettingsManager settings,
    LlamaSharpModelManager models)
{
    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var config = await settings.GetLlamaSharpAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.Model))
        {
            return new EngineStatus(false, "llamasharp://local", null, "Nenhum modelo LLamaSharp selecionado.");
        }

        var path = models.ResolveModelPath(config);
        return File.Exists(path)
            ? new EngineStatus(true, "llamasharp://local", config.Model, null)
            : new EngineStatus(false, "llamasharp://local", config.Model, "Modelo LLamaSharp não encontrado.");
    }

    public async Task<string> CompleteChatAsync(
        IReadOnlyList<LlamaChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder();
        await foreach (var token in StreamChatAsync(messages, cancellationToken))
        {
            answer.Append(token);
        }

        return answer.ToString();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<LlamaChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = await settings.GetLlamaSharpAsync(cancellationToken);
        var path = models.ResolveModelPath(config);
        if (string.IsNullOrWhiteSpace(config.Model) || !File.Exists(path))
        {
            throw new InvalidOperationException("Seleciona um modelo GGUF válido nas definições LLamaSharp.");
        }

        var parameters = new ModelParams(path)
        {
            ContextSize = config.ContextSize,
            GpuLayerCount = config.GpuLayerCount,
            Threads = config.Threads,
            BatchSize = config.BatchSize
        };

        // shortcut: model loads per request, upgrade to a shared cache when concurrent inference matters.
        using var model = LLamaWeights.LoadFromFile(parameters);
        using var context = model.CreateContext(parameters);
        var executor = new InteractiveExecutor(context);
        var history = new ChatHistory();
        var lastUserMessage = messages.LastOrDefault(message => message.Role == "user");

        foreach (var message in messages)
        {
            if (ReferenceEquals(message, lastUserMessage))
            {
                break;
            }

            history.AddMessage(ToAuthorRole(message.Role), message.Content);
        }

        if (lastUserMessage is null)
        {
            yield break;
        }

        var inference = new InferenceParams
        {
            MaxTokens = 512,
            AntiPrompts = ["<|im_end|>", "User:"],
            SamplingPipeline = new DefaultSamplingPipeline()
        };

        var session = new ChatSession(executor, history)
            .WithHistoryTransform(new PromptTemplateTransformer(model));
        var firstOutput = new System.Text.StringBuilder();
        const string assistantMarker = "Assistant:";

        await foreach (var token in session
            .ChatAsync(new ChatHistory.Message(AuthorRole.User, lastUserMessage.Content), inference)
            .WithCancellation(cancellationToken))
        {
            if (firstOutput.Length < assistantMarker.Length)
            {
                firstOutput.Append(token);
                var buffered = firstOutput.ToString();
                if (assistantMarker.StartsWith(buffered, StringComparison.Ordinal))
                {
                    if (buffered.Length < assistantMarker.Length)
                    {
                        continue;
                    }

                    yield return buffered[assistantMarker.Length..];
                    continue;
                }

                yield return buffered;
                firstOutput.Clear();
                continue;
            }

            yield return token;
        }
    }

    private static AuthorRole ToAuthorRole(string role) => role switch
    {
        "system" => AuthorRole.System,
        "assistant" => AuthorRole.Assistant,
        _ => AuthorRole.User
    };
}
