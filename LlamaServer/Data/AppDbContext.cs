using Microsoft.EntityFrameworkCore;
using LlamaServer.Models;

namespace LlamaServer.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    public DbSet<AppSetting> Settings => Set<AppSetting>();

    public DbSet<DocumentFile> DocumentFiles => Set<DocumentFile>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>()
            .HasIndex(user => user.Username)
            .IsUnique();

        modelBuilder.Entity<Conversation>()
            .HasOne(conversation => conversation.User)
            .WithMany(user => user.Conversations)
            .HasForeignKey(conversation => conversation.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Conversation>()
            .HasMany(conversation => conversation.Messages)
            .WithOne(message => message.Conversation)
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AppSetting>()
            .HasKey(setting => setting.Key);

        modelBuilder.Entity<DocumentFile>()
            .HasOne(document => document.OwnerUser)
            .WithMany()
            .HasForeignKey(document => document.OwnerUserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<DocumentFile>()
            .HasMany(document => document.Chunks)
            .WithOne(chunk => chunk.DocumentFile)
            .HasForeignKey(chunk => chunk.DocumentFileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DocumentChunk>()
            .HasIndex(chunk => new { chunk.DocumentFileId, chunk.ChunkIndex })
            .IsUnique();
    }
}
