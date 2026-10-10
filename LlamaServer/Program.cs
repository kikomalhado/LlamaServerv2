using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LlamaServer.Components;
using LlamaServer.Data;
using LlamaServer.Models;
using LlamaServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LlamaServerOptions>(
    builder.Configuration.GetSection(LlamaServerOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                      ?? "Data Source=llama-server.db"));

builder.Services.AddHttpClient<LlamaCppClient>();
builder.Services.AddScoped(sp =>
{
    var navigation = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
});
builder.Services.AddScoped<ModelManager>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseAntiforgery();

var api = app.MapGroup("/api");

api.MapPost("/chat", async (
    ChatRequest request,
    AppDbContext db,
    LlamaCppClient llama,
    CancellationToken cancellationToken) =>
{
    var conversation = await GetOrCreateConversationAsync(db, request.ConversationId, cancellationToken);
    var userMessage = new ChatMessage
    {
        ConversationId = conversation.Id,
        Role = "user",
        Content = request.Message.Trim()
    };

    db.Messages.Add(userMessage);
    await db.SaveChangesAsync(cancellationToken);

    var history = await db.Messages
        .Where(message => message.ConversationId == conversation.Id)
        .OrderBy(message => message.CreatedAt)
        .Select(message => new LlamaChatMessage(message.Role, message.Content))
        .ToListAsync(cancellationToken);

    var answer = await llama.CompleteChatAsync(history, cancellationToken);

    db.Messages.Add(new ChatMessage
    {
        ConversationId = conversation.Id,
        Role = "assistant",
        Content = answer
    });
    conversation.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new ChatResponse(conversation.Id, answer));
});

api.MapPost("/chat/stream", async (
    ChatRequest request,
    HttpContext httpContext,
    AppDbContext db,
    LlamaCppClient llama,
    CancellationToken cancellationToken) =>
{
    var conversation = await GetOrCreateConversationAsync(db, request.ConversationId, cancellationToken);
    db.Messages.Add(new ChatMessage
    {
        ConversationId = conversation.Id,
        Role = "user",
        Content = request.Message.Trim()
    });
    await db.SaveChangesAsync(cancellationToken);

    var history = await db.Messages
        .Where(message => message.ConversationId == conversation.Id)
        .OrderBy(message => message.CreatedAt)
        .Select(message => new LlamaChatMessage(message.Role, message.Content))
        .ToListAsync(cancellationToken);

    httpContext.Response.Headers.ContentType = "text/event-stream";
    await httpContext.Response.WriteAsync($"event: conversation\ndata: {conversation.Id}\n\n", cancellationToken);

    var answer = "";
    await foreach (var token in llama.StreamChatAsync(history, cancellationToken))
    {
        answer += token;
        await httpContext.Response.WriteAsync($"data: {Sse.Encode(token)}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    db.Messages.Add(new ChatMessage
    {
        ConversationId = conversation.Id,
        Role = "assistant",
        Content = answer
    });
    conversation.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    await httpContext.Response.WriteAsync("event: done\ndata: ok\n\n", cancellationToken);
});

api.MapGet("/models", async (ModelManager models, CancellationToken cancellationToken) =>
    Results.Ok(await models.ListModelsAsync(cancellationToken)));

api.MapGet("/engine/status", async (LlamaCppClient llama, CancellationToken cancellationToken) =>
    Results.Ok(await llama.GetStatusAsync(cancellationToken)));

api.MapPost("/models/load", async (
    LoadModelRequest request,
    ModelManager models,
    CancellationToken cancellationToken) =>
{
    var result = await models.LoadModelAsync(request.Model, cancellationToken);
    return result is null
        ? Results.NotFound(new { error = "Modelo nao encontrado na pasta configurada." })
        : Results.Ok(result);
});

api.MapGet("/conversations", async (AppDbContext db, CancellationToken cancellationToken) =>
    await db.Conversations
        .OrderByDescending(conversation => conversation.UpdatedAt)
        .Select(conversation => new ConversationSummary(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt))
        .ToListAsync(cancellationToken));

api.MapGet("/conversations/{id:guid}", async (
    Guid id,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var conversation = await db.Conversations
        .Include(conversation => conversation.Messages)
        .FirstOrDefaultAsync(conversation => conversation.Id == id, cancellationToken);

    return conversation is null
        ? Results.NotFound()
        : Results.Ok(new ConversationDetail(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.Messages
                .OrderBy(message => message.CreatedAt)
                .Select(message => new MessageDto(
                    message.Id,
                    message.Role,
                    message.Content,
                    message.CreatedAt))
                .ToList()));
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task<Conversation> GetOrCreateConversationAsync(
    AppDbContext db,
    Guid? conversationId,
    CancellationToken cancellationToken)
{
    if (conversationId is { } id)
    {
        var existing = await db.Conversations.FindAsync([id], cancellationToken);
        if (existing is not null)
        {
            return existing;
        }
    }

    var conversation = new Conversation
    {
        Title = "Nova conversa"
    };
    db.Conversations.Add(conversation);
    await db.SaveChangesAsync(cancellationToken);
    return conversation;
}
