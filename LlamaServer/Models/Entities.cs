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
