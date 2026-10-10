namespace LlamaServer.Models;

public sealed record ChatRequest(string Message, Guid? ConversationId = null);

public sealed record ChatResponse(Guid ConversationId, string Message);

public sealed record ConversationSummary(
    Guid Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ConversationDetail(
    Guid Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MessageDto> Messages);

public sealed record MessageDto(
    Guid Id,
    string Role,
    string Content,
    DateTime CreatedAt);

public sealed record LoadModelRequest(string Model);

public sealed record ModelInfo(
    string Name,
    string Path,
    long SizeBytes,
    bool IsActive);

public sealed record LoadModelResponse(
    string ActiveModel,
    string Path,
    string EngineUrl,
    string Note);

public sealed record LlamaChatMessage(string Role, string Content);

public sealed record EngineStatus(
    bool IsRunning,
    string EngineUrl,
    string? CurrentModel,
    string? Error);
