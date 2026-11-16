using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LLama.Native;
using LlamaServer.Components;
using LlamaServer.Data;
using LlamaServer.Models;
using LlamaServer.Services;

var builder = WebApplication.CreateBuilder(args);

// CUDA is attempted first; LLamaSharp falls back to the CPU backend when it is unavailable.
NativeLibraryConfig.All.WithCuda(true).WithAutoFallback(true);

builder.Services.Configure<LlamaServerOptions>(
    builder.Configuration.GetSection(LlamaServerOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                      ?? "Data Source=llama-server.db"));

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<LlamaCppClient>();
builder.Services.AddScoped<LlamaSharpModelManager>();
builder.Services.AddScoped<LlamaSharpClient>();
builder.Services.AddScoped(sp =>
{
    var navigation = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    var client = new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };

    var cookieHeader = httpContextAccessor.HttpContext?.Request.Headers.Cookie.ToString();
    if (!string.IsNullOrWhiteSpace(cookieHeader))
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
    }

    return client;
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(UserRole.Admin));
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<ModelManager>();
builder.Services.AddScoped<SettingsManager>();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureSchemaUpgradesAsync(db);
    await SeedAdminUserAsync(db);
}
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/auth/login", async (
    [FromForm] string username,
    [FromForm] string password,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var normalized = NormalizeUsername(username);
    if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/login?error=Preenche+utilizador+e+palavra-passe");
    }

    var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Username == normalized, cancellationToken);
    if (user is null)
    {
        return Results.Redirect("/login?error=Credenciais+invalidas");
    }

    if (!VerifyPassword(password, user.PasswordSalt, user.PasswordHash))
    {
        // Development recovery for the seeded admin after an old database keeps another hash.
        if (!(app.Environment.IsDevelopment()
            && normalized == "admin"
            && password == "admin123"))
        {
            return Results.Redirect("/login?error=Credenciais+invalidas");
        }

        var (salt, hash) = HashPassword(password);
        user.PasswordSalt = salt;
        user.PasswordHash = hash;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    await SignInUserAsync(httpContext, user);
    return Results.Redirect("/");
}).AllowAnonymous();

app.MapPost("/auth/signup", async (
    [FromForm] string username,
    [FromForm] string displayName,
    [FromForm] string password,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var normalized = NormalizeUsername(username);
    var trimmedName = displayName.Trim();
    if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(trimmedName) || password.Length < 6)
    {
        return Results.Redirect("/signup?error=Preenche+todos+os+campos+e+usa+uma+password+com+6+ou+mais+caracteres");
    }

    var exists = await db.Users.AnyAsync(candidate => candidate.Username == normalized, cancellationToken);
    if (exists)
    {
        return Results.Redirect("/signup?error=Utilizador+ja+existe");
    }

    var role = await db.Users.AnyAsync(cancellationToken) ? UserRole.User : UserRole.Admin;
    var (salt, hash) = HashPassword(password);
    var user = new UserAccount
    {
        Username = normalized,
        DisplayName = trimmedName,
        PasswordSalt = salt,
        PasswordHash = hash,
        Role = role
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(cancellationToken);
    await SignInUserAsync(httpContext, user);
    return Results.Redirect("/");
}).AllowAnonymous();

app.MapPost("/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();

var authApi = api.MapGroup("/auth");
authApi.MapGet("/me", async (HttpContext httpContext, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.FindAsync([userId.Value], cancellationToken);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(ToAuthUser(user));
});

api.MapPost("/chat", async (
    ChatRequest request,
    HttpContext httpContext,
    AppDbContext db,
    LlamaCppClient llama,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var conversation = await GetOrCreateConversationAsync(db, userId.Value, request.ConversationId, cancellationToken);
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
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var conversation = await GetOrCreateConversationAsync(db, userId.Value, request.ConversationId, cancellationToken);
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

api.MapGet("/llamasharp/models", async (LlamaSharpModelManager models, CancellationToken cancellationToken) =>
    Results.Ok(await models.ListModelsAsync(cancellationToken)));

api.MapPost("/llamasharp/models/load", async (
    LoadModelRequest request,
    LlamaSharpModelManager models,
    CancellationToken cancellationToken) =>
{
    var selected = await models.SelectModelAsync(request.Model, cancellationToken);
    return selected
        ? Results.Ok(await models.ListModelsAsync(cancellationToken))
        : Results.NotFound(new { error = "Modelo LLamaSharp não encontrado nas pastas configuradas." });
});

api.MapPut("/llamasharp/models/access", async (
    UpdateLlamaSharpModelAccessRequest request,
    SettingsManager settings,
    CancellationToken cancellationToken) =>
{
    await settings.SetLlamaSharpAllowedModelsAsync(request.Models, cancellationToken);
    return Results.Ok();
}).RequireAuthorization("AdminOnly");

api.MapGet("/engine/status", async (LlamaCppClient llama, CancellationToken cancellationToken) =>
    Results.Ok(await llama.GetStatusAsync(cancellationToken)));

api.MapGet("/settings", async (SettingsManager settings, CancellationToken cancellationToken) =>
    Results.Ok(await settings.GetAsync(cancellationToken)));

api.MapGet("/settings/llamasharp", async (SettingsManager settings, CancellationToken cancellationToken) =>
    Results.Ok(await settings.GetLlamaSharpAsync(cancellationToken)));

api.MapPut("/settings/llamasharp", async (
    UpdateLlamaSharpSettingsRequest request,
    SettingsManager settings,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await settings.UpdateLlamaSharpAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization("AdminOnly");

api.MapPut("/settings", async (
    UpdateServerSettingsRequest request,
    SettingsManager settings,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await settings.UpdateAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization("AdminOnly");

api.MapPost("/models/load", async (
    LoadModelRequest request,
    ModelManager models,
    CancellationToken cancellationToken) =>
{
    var result = await models.LoadModelAsync(request.Model, cancellationToken);
    return result is null
        ? Results.NotFound(new { error = "Modelo nao encontrado na pasta configurada." })
        : Results.Ok(result);
}).RequireAuthorization("AdminOnly");

api.MapPut("/account/profile", async (
    UpdateProfileRequest request,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.FindAsync([userId.Value], cancellationToken);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var name = request.DisplayName.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new ErrorResponse("Nome invalido."));
    }

    user.DisplayName = name;
    user.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    await SignInUserAsync(httpContext, user);
    return Results.Ok(ToAuthUser(user));
});

api.MapPut("/account/password", async (
    ChangePasswordRequest request,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.FindAsync([userId.Value], cancellationToken);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    if (!VerifyPassword(request.CurrentPassword, user.PasswordSalt, user.PasswordHash))
    {
        return Results.BadRequest(new ErrorResponse("Password atual invalida."));
    }

    if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
    {
        return Results.BadRequest(new ErrorResponse("A nova password deve ter pelo menos 6 caracteres."));
    }

    var (salt, hash) = HashPassword(request.NewPassword);
    user.PasswordSalt = salt;
    user.PasswordHash = hash;
    user.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok();
});

api.MapGet("/admin/users", async (AppDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.Users
        .OrderBy(user => user.Username)
        .Select(user => new AdminUserDto(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Role,
            user.CreatedAt,
            user.UpdatedAt))
        .ToListAsync(cancellationToken)))
    .RequireAuthorization("AdminOnly");

api.MapPut("/admin/users/{id:guid}", async (
    Guid id,
    AdminUpdateUserRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
    if (user is null)
    {
        return Results.NotFound(new ErrorResponse("Utilizador nao encontrado."));
    }

    var role = request.Role.Trim().ToLowerInvariant();
    if (role is not (UserRole.Admin or UserRole.User))
    {
        return Results.BadRequest(new ErrorResponse("Role invalida. Usa 'admin' ou 'user'."));
    }

    if (user.Role == UserRole.Admin && role == UserRole.User)
    {
        var adminCount = await db.Users.CountAsync(candidate => candidate.Role == UserRole.Admin, cancellationToken);
        if (adminCount <= 1)
        {
            return Results.BadRequest(new ErrorResponse("Nao e permitido remover o ultimo admin."));
        }
    }

    var name = request.DisplayName.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return Results.BadRequest(new ErrorResponse("Nome invalido."));
    }

    user.DisplayName = name;
    user.Role = role;
    user.UpdatedAt = DateTime.UtcNow;

    if (!string.IsNullOrWhiteSpace(request.NewPassword))
    {
        if (request.NewPassword.Length < 6)
        {
            return Results.BadRequest(new ErrorResponse("A nova password deve ter pelo menos 6 caracteres."));
        }

        var (salt, hash) = HashPassword(request.NewPassword);
        user.PasswordSalt = salt;
        user.PasswordHash = hash;
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new AdminUserDto(
        user.Id,
        user.Username,
        user.DisplayName,
        user.Role,
        user.CreatedAt,
        user.UpdatedAt));
}).RequireAuthorization("AdminOnly");

api.MapDelete("/admin/users/{id:guid}", async (
    Guid id,
    ClaimsPrincipal principal,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
    if (user is null)
    {
        return Results.NotFound(new ErrorResponse("Utilizador nao encontrado."));
    }

    var currentUserId = GetUserId(principal);
    if (currentUserId == user.Id)
    {
        return Results.BadRequest(new ErrorResponse("Nao podes apagar a tua propria conta."));
    }

    if (user.Role == UserRole.Admin)
    {
        var adminCount = await db.Users.CountAsync(candidate => candidate.Role == UserRole.Admin, cancellationToken);
        if (adminCount <= 1)
        {
            return Results.BadRequest(new ErrorResponse("Nao e permitido apagar o ultimo admin."));
        }
    }

    db.Users.Remove(user);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok();
}).RequireAuthorization("AdminOnly");

api.MapGet("/conversations", async (HttpContext httpContext, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var items = await db.Conversations
        .Where(conversation => conversation.UserId == userId)
        .OrderByDescending(conversation => conversation.UpdatedAt)
        .Select(conversation => new ConversationSummary(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt))
        .ToListAsync(cancellationToken);

    return Results.Ok(items);
});

api.MapGet("/conversations/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var conversation = await db.Conversations
        .Include(candidate => candidate.Messages)
        .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);

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

api.MapPut("/conversations/{id:guid}", async (
    Guid id,
    RenameConversationRequest request,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var title = request.Title.Trim();
    if (string.IsNullOrWhiteSpace(title) || title.Length > 120)
    {
        return Results.BadRequest(new ErrorResponse("O título deve ter entre 1 e 120 caracteres."));
    }

    var conversation = await db.Conversations
        .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    conversation.Title = title;
    conversation.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok();
});

api.MapDelete("/conversations/{id:guid}", async (
    Guid id,
    HttpContext httpContext,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(httpContext.User);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var conversation = await db.Conversations
        .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    db.Conversations.Remove(conversation);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok();
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task<Conversation> GetOrCreateConversationAsync(
    AppDbContext db,
    Guid userId,
    Guid? conversationId,
    CancellationToken cancellationToken)
{
    if (conversationId is { } id)
    {
        var existing = await db.Conversations
            .FirstOrDefaultAsync(conversation => conversation.Id == id && conversation.UserId == userId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }
    }

    var conversation = new Conversation
    {
        Title = "Nova conversa",
        UserId = userId
    };
    db.Conversations.Add(conversation);
    await db.SaveChangesAsync(cancellationToken);
    return conversation;
}

static Guid? GetUserId(ClaimsPrincipal principal)
{
    var idRaw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(idRaw, out var userId) ? userId : null;
}

static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

static async Task SignInUserAsync(HttpContext httpContext, UserAccount user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new("display_name", user.DisplayName),
        new(ClaimTypes.Role, user.Role)
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(14)
        });
}

static AuthUserDto ToAuthUser(UserAccount user) =>
    new(user.Id, user.Username, user.DisplayName, user.Role);

static (string Salt, string Hash) HashPassword(string password)
{
    var saltBytes = RandomNumberGenerator.GetBytes(16);
    var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 120_000, HashAlgorithmName.SHA256, 32);
    return (Convert.ToBase64String(saltBytes), Convert.ToBase64String(hashBytes));
}

static bool VerifyPassword(string password, string salt, string expectedHash)
{
    byte[] saltBytes;
    byte[] expectedHashBytes;

    try
    {
        saltBytes = Convert.FromBase64String(salt);
        expectedHashBytes = Convert.FromBase64String(expectedHash);
    }
    catch (FormatException)
    {
        return false;
    }

    var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 120_000, HashAlgorithmName.SHA256, expectedHashBytes.Length);
    return CryptographicOperations.FixedTimeEquals(actualHash, expectedHashBytes);
}

static async Task EnsureSchemaUpgradesAsync(AppDbContext db)
{
    await db.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS Users (
            Id TEXT NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
            Username TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            PasswordHash TEXT NOT NULL,
            PasswordSalt TEXT NOT NULL,
            Role TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """);

    await EnsureColumnExistsAsync(db, "Conversations", "UserId", "ALTER TABLE Conversations ADD COLUMN UserId TEXT NULL;");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users (Username);");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Conversations_UserId ON Conversations (UserId);");
}

static async Task EnsureColumnExistsAsync(AppDbContext db, string table, string column, string alterSql)
{
    await using var connection = db.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({table});";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
    }

    await db.Database.ExecuteSqlRawAsync(alterSql);
}

static async Task SeedAdminUserAsync(AppDbContext db)
{
    var admin = await db.Users.FirstOrDefaultAsync(user => user.Role == UserRole.Admin);
    if (admin is null)
    {
        var (salt, hash) = HashPassword("admin123");
        admin = new UserAccount
        {
            Username = "admin",
            DisplayName = "Administrador",
            Role = UserRole.Admin,
            PasswordSalt = salt,
            PasswordHash = hash
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        Console.WriteLine("[LlamaServer] Utilizador admin criado: username=admin password=admin123");
    }

    await db.Database.ExecuteSqlRawAsync(
        "UPDATE Conversations SET UserId = {0} WHERE UserId IS NULL;",
        admin.Id.ToString());
}
