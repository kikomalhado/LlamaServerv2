namespace LlamaServer.Models;

public static class UserRole
{
    public const string Admin = "admin";
    public const string User = "user";
}

public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string PasswordSalt { get; set; } = "";

    public string Role { get; set; } = UserRole.User;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Conversation> Conversations { get; set; } = [];
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "Nova conversa";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Guid? UserId { get; set; }

    public UserAccount? User { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public Conversation? Conversation { get; set; }

    public string Role { get; set; } = "user";

    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AppSetting
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}

public static class DocumentStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}

public sealed class DocumentFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? OwnerUserId { get; set; }

    public UserAccount? OwnerUser { get; set; }

    public string OriginalName { get; set; } = "";

    public string StoredPath { get; set; } = "";

    public string Extension { get; set; } = "";

    public long SizeBytes { get; set; }

    public bool IsShared { get; set; }

    public string Status { get; set; } = DocumentStatus.Pending;

    public string? ErrorMessage { get; set; }

    public string? EmbeddingModel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<DocumentChunk> Chunks { get; set; } = [];
}

public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentFileId { get; set; }

    public DocumentFile? DocumentFile { get; set; }

    public int ChunkIndex { get; set; }

    public string Text { get; set; } = "";

    public byte[] Embedding { get; set; } = [];

    public int? PageNumber { get; set; }

    public string? Section { get; set; }
}
