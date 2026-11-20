namespace LlamaServer.Models;

public sealed record ChatRequest(string Message, Guid? ConversationId = null);

public sealed record ChatResponse(Guid ConversationId, string Message);

public sealed record RenameConversationRequest(string Title);

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
    bool IsActive,
    bool IsAllowed);

public sealed record UpdateLlamaSharpModelAccessRequest(
    IReadOnlyList<string> Models);

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

public sealed record ServerSettingsDto(
    string Engine,
    string EngineUrl,
    string ModelDirectory);

public sealed record UpdateServerSettingsRequest(
    string Engine,
    string EngineUrl,
    string ModelDirectory);

public sealed record LlamaSharpConfig(
    string ModelDirectory,
    string Model,
    uint ContextSize,
    int GpuLayerCount,
    int Threads,
    uint BatchSize);

public sealed record UpdateLlamaSharpSettingsRequest(
    string ModelDirectory,
    uint ContextSize,
    int GpuLayerCount,
    int Threads,
    uint BatchSize);

public sealed record AuthUserDto(
    Guid Id,
    string Username,
    string DisplayName,
    string Role);

public sealed record SignupRequest(
    string Username,
    string DisplayName,
    string Password);

public sealed record LoginRequest(
    string Username,
    string Password);

public sealed record UpdateProfileRequest(string DisplayName);

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);

public sealed record AdminUserDto(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AdminUpdateUserRequest(
    string DisplayName,
    string Role,
    string? NewPassword);

public sealed record ErrorResponse(string Error);

public sealed record DocumentInfo(
    Guid Id,
    string OriginalName,
    string Extension,
    long SizeBytes,
    bool IsShared,
    string Status,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UpdateDocumentSharingRequest(bool IsShared);

public sealed record DocumentSettingsDto(
    string DocumentsRoot,
    long MaxFileSizeBytes,
    long UserQuotaBytes,
    long GlobalQuotaBytes,
    string EmbeddingModelDirectory,
    string EmbeddingModel,
    int IndexingConcurrency,
    int ChunkSize,
    int ChunkOverlap,
    int ResultCount);

public sealed record UpdateDocumentSettingsRequest(
    string DocumentsRoot,
    long MaxFileSizeBytes,
    long UserQuotaBytes,
    long GlobalQuotaBytes,
    string EmbeddingModelDirectory,
    string EmbeddingModel,
    int IndexingConcurrency,
    int ChunkSize,
    int ChunkOverlap,
    int ResultCount);
