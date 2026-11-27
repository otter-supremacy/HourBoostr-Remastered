using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using HourBoostr.Members;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var config = builder.Configuration;
var directory = Path.GetFullPath(config["Members:Data"] ?? throw new InvalidOperationException("Configure Members:Data."));
var origin = new Uri(config["Members:Origin"] ?? "http://localhost:5481");
var socket = config["Members:Socket"];
var testing = builder.Environment.IsEnvironment("Testing");
var live = config.GetValue<bool>("Members:LiveSteam");
var open = config.GetValue<bool>("Members:OpenRegistration");
var passwords = (open || testing) && !live && config.GetValue("Members:PasswordAccounts", open);
var dashboard = origin.IsLoopback ? "/" : "/dashboard";
var capacity = config.GetValue("Members:Capacity", 1);
var port = config.GetValue("Members:Port", 5481);
var clientId = config["Members:GoogleClientId"];
var clientSecret = config["Members:GoogleClientSecret"];
var google = !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
var allowlist = new HashSet<string>((config["Members:TesterEmails"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
if ((!origin.IsLoopback && origin.Scheme != "https") || origin.AbsolutePath != "/" || origin.Query.Length > 0
    || origin.Fragment.Length > 0 || origin.UserInfo.Length > 0 || capacity is < 1 or > 20
    || (testing && (!origin.IsLoopback || live || open)) || (open && (!google || origin.Scheme != "https")))
    throw new InvalidOperationException("Invalid member service configuration.");
var originText = origin.GetLeftPart(UriPartial.Authority);
var sessionCookie = origin.Scheme == "https" ? "__Host-hbr-member" : "hbr-member-local";
Directory.CreateDirectory(directory);
var keyDirectory = Directory.CreateDirectory(Path.Combine(directory, "keys"));
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 8 * 1024;
    options.ListenLocalhost(port);
    if (socket is not null) options.ListenUnixSocket(socket);
});
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Error);
builder.Services.AddDataProtection().SetApplicationName("HourBoostr.Members").PersistKeysToFileSystem(keyDirectory);
builder.Services.AddSingleton(_ => new MemberStore(directory));
if (passwords) builder.Services.AddSingleton<PasswordAccounts>();
builder.Services.AddSingleton<MemberChatAi>();
builder.Services.AddSingleton(p => new MemberWorker(p.GetRequiredService<MemberStore>(),
    p.GetRequiredService<IDataProtectionProvider>(), live, capacity, p.GetRequiredService<MemberChatAi>()));
var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = sessionCookie;
    options.Cookie.SecurePolicy = origin.Scheme == "https" ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = context.Principal?.FindFirstValue("hbr-session");
        if (id is not null && session is not null && context.HttpContext.RequestServices.GetRequiredService<MemberStore>().Authorize(id, session)) return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync();
    };
});
if (google)
{
    auth.AddOpenIdConnect("Google", options =>
    {
        options.Authority = "https://accounts.google.com";
        options.ClientId = clientId!;
        options.ClientSecret = clientSecret!;
        options.CallbackPath = "/auth/callback";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        if (origin.IsLoopback && origin.Scheme == "http")
        {
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
        }
        options.Events.OnTicketReceived = context =>
        {
            var member = context.Principal;
            var email = member?.FindFirstValue("email");
            var subject = member?.FindFirstValue("sub");
            var verified = string.Equals(member?.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
            if (email is not null && subject is not null && verified && (open || allowlist.Contains(email)) &&
                context.HttpContext.RequestServices.GetRequiredService<MemberStore>().SignIn("google:" + subject, email) is { } session)
            {
                context.Principal = SessionPrincipal(session);
                return Task.CompletedTask;
            }
            context.HandleResponse();
            context.Response.Redirect(dashboard + "?auth=denied");
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect(dashboard + "?auth=failed");
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
            _ => new() { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 12, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("password", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = testing ? 50 : 10, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("operations", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new() { PermitLimit = 12, Window = TimeSpan.FromMinutes(1) }));
});
var app = builder.Build();
_ = app.Services.GetRequiredService<MemberStore>();
app.Use(async (context, next) =>
{
    var localSocket = context.Connection.LocalPort == 0 && context.Connection.LocalIpAddress is null;
    var management = context.Request.Path.StartsWithSegments("/internal");
    if (localSocket != management) { context.Response.StatusCode = 404; return; }
    if (!localSocket)
    {
        // Use the configured browser origin for OIDC and host validation. Forwarded headers are not trusted.
        if (!string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            return;
        }
        context.Request.Scheme = origin.Scheme;
        if (!origin.IsLoopback && context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer)
            && IPAddress.TryParse(context.Request.Headers["X-HBR-Edge-IP"].ToString(), out var visitor))
            context.Connection.RemoteIpAddress = visitor;
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (context.Request.Method is not ("GET" or "HEAD")
            && (context.Request.Headers.Origin != originText || context.Request.Headers["X-HBR-Member"] != "1"))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
foreach (var page in new[] { "/dashboard", "/login", "/signup", "/reset" })
    app.MapGet(page, () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html; charset=utf-8"));
app.MapGet("/auth/google", () => google
    ? Results.Challenge(new AuthenticationProperties { RedirectUri = dashboard }, ["Google"])
    : Results.StatusCode(503)).RequireRateLimiting("auth");
app.MapGet("/api/member/config", () => new { google, simulated = !live, registrationOpen = open, passwords });
if (passwords)
    app.MapPost("/auth/password/{operation}", async (string operation, Credentials input, HttpContext context, PasswordAccounts accounts, MemberStore store) =>
        operation is "register" or "login" or "recover"
            ? await accounts.Handle(operation, input, context, store, SessionPrincipal)
            : Results.NotFound()).RequireRateLimiting("password");
if (testing)
{
    app.MapPost("/auth/test/{name}", async (string name, HttpContext context, MemberStore store) =>
    {
        if (name is not ("alice" or "bob")) return Results.NotFound();
        if (store.SignIn("test:" + name, name + "@example.test") is not { } session) return Results.StatusCode(403);
        await context.SignInAsync(SessionPrincipal(session));
        return Results.NoContent();
    });
}

var api = app.MapGroup("/api/member");
api.AddEndpointFilter(async (context, next) => Id(context.HttpContext) is null ? Results.Unauthorized() : await next(context));
api.MapGet("/me", (HttpContext context, MemberStore store) =>
    store.Get(Id(context)!) is { } member ? Results.Ok(member) : Results.Unauthorized());
api.MapGet("/appearance", (HttpContext context, MemberStore store) => store.Appearance(Id(context)!) ?? new MemberAppearance());
api.MapPut("/appearance", (MemberAppearance input, HttpContext context, MemberStore store) =>
{
    if (input.Validate() is { } error) return Results.BadRequest(new { error });
    var appearance = input with { CustomText = input.CustomText.Trim() };
    return store.SaveAppearance(Id(context)!, appearance) ? Results.Ok(appearance) : Results.Unauthorized();
}).RequireRateLimiting("operations");
api.MapGet("/chat", (HttpContext context, MemberStore store, MemberChatAi ai) =>
    new { settings = store.Chat(Id(context)!), aiConfigured = ai.Configured(Id(context)!, "gpt"), qwenConfigured = ai.Configured(Id(context)!, "qwen"), gptUsed = store.AiUsed(Id(context)!, "gpt"), qwenUsed = store.AiUsed(Id(context)!, "qwen") });
api.MapPut("/chat", (MemberChatSettings input, HttpContext context, MemberStore store, MemberChatAi ai) =>
{
    if (input.Validate(ai.Configured(Id(context)!, "gpt")) is { } error) return Results.BadRequest(new { error });
    return store.SaveChat(Id(context)!, input) ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("operations");
api.MapPut("/chat/key/{model}", (string model, MemberKeyInput input, HttpContext context, MemberChatAi ai) =>
{
    if (!MemberChatAi.ValidModel(model) || input.Key is null || !MemberChatAi.ValidKey(input.Key))
        return Results.BadRequest(new { error = "Enter a valid Groq API key." });
    return ai.Save(Id(context)!, model, input.Key) ? Results.NoContent() : Results.Unauthorized();
}).RequireRateLimiting("operations");
api.MapDelete("/chat/key/{model}", (string model, HttpContext context, MemberChatAi ai) =>
{
    if (!MemberChatAi.ValidModel(model)) return Results.BadRequest();
    ai.Save(Id(context)!, model, null);
    return Results.NoContent();
}).RequireRateLimiting("operations");
api.MapPost("/chat/preview", async (MemberAiPreview input, HttpContext context, MemberStore store, MemberChatAi ai) =>
{
    if (input.Message is null || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 500)
        return Results.BadRequest(new { error = "Enter a sample message of up to 500 characters." });
    var id = Id(context)!;
    if (!ai.Configured(id, "gpt")) return Conflict("Save your GPT Groq key before previewing.");
    var reply = await ai.Generate(id, input.Message, store.Chat(id), context.RequestAborted);
    return reply is null ? Conflict("Groq could not generate a reply. Check the key and try again after the cooldown.")
        : Results.Ok(new { reply });
}).RequireRateLimiting("operations");
api.MapGet("/chat/regulars", (HttpContext context, MemberWorker worker) => worker.Regulars(Id(context)!));
api.MapPost("/chat/regulars/approve", async (RegularApproval input, HttpContext context, MemberWorker worker) =>
    ulong.TryParse(input.SteamId, out var sender) && await worker.ApproveRegular(Id(context)!, sender)
        ? Results.NoContent() : Conflict("The member is no longer eligible, the connection is unavailable, or the daily request was already used."))
    .RequireRateLimiting("operations");
api.MapGet("/games", (HttpContext context, MemberStore store) => store.Library(Id(context)!));
api.MapGet("/steam/qr", async (HttpContext context, MemberWorker worker) =>
    await worker.Qr(Id(context)!) is { } qr ? Results.File(qr, "image/png") : Results.NotFound());
api.MapPost("/steam/link", async (HttpContext context, MemberWorker worker) =>
    await worker.Link(Id(context)!) ? Results.Accepted() : Conflict("Already linked or pairing capacity is full.")).RequireRateLimiting("operations");
api.MapDelete("/steam/link", async (HttpContext context, MemberWorker worker) =>
{
    await worker.Stop(Id(context)!, unlink: true);
    return Results.NoContent();
}).RequireRateLimiting("operations");
api.MapPut("/game", async (GameChoice game, HttpContext context, MemberWorker worker) =>
    await worker.SetGame(Id(context)!, game.AppId) ? Results.NoContent() : Conflict("Choose one owned game.")).RequireRateLimiting("operations");
api.MapPost("/start", async (HttpContext context, MemberWorker worker) =>
    await worker.Start(Id(context)!) ? Results.Accepted() : Conflict("Choose a game first, or wait for an open worker slot.")).RequireRateLimiting("operations");
api.MapPost("/pause", async (HttpContext context, MemberWorker worker) =>
{
    await worker.Stop(Id(context)!, unlink: false);
    return Results.NoContent();
}).RequireRateLimiting("operations");
api.MapPost("/logout", async (HttpContext context, MemberStore store) =>
{
    store.SignOut(context.User.FindFirstValue("hbr-session")!);
    await context.SignOutAsync();
    return Results.NoContent();
});

app.MapGet("/internal/members", (string? after, MemberStore store) =>
{
    if (after is not null && !Guid.TryParseExact(after, "N", out _)) return Results.BadRequest();
    var rows = store.List(after ?? "");
    return Results.Ok(new { members = rows.Take(25), next = rows.Count > 25 ? rows[24].Id : null });
});
app.MapPost("/internal/members/{id}/{action}", async (string id, string action, Moderation input, MemberWorker worker) =>
{
    if (!Guid.TryParseExact(id, "N", out _) || action is not ("ban" or "unban")
        || !Guid.TryParseExact(input.DeviceId, "N", out _)) return Results.BadRequest();
    return await worker.Moderate(id, action == "ban", input.DeviceId) ? Results.NoContent() : Results.NotFound();
});
app.MapPost("/internal/backup", (MemberStore store) =>
{
    var candidate = Path.Combine(directory, "members.backup.next");
    var backup = Path.Combine(directory, "members.backup.sqlite");
    File.Delete(candidate);
    store.Backup(candidate);
    File.Move(candidate, backup, overwrite: true);
    return Results.NoContent();
});

await app.StartAsync();
if (socket is not null && !OperatingSystem.IsWindows())
    File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
await app.WaitForShutdownAsync();
await app.DisposeAsync();

static string? Id(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier);
static ClaimsPrincipal SessionPrincipal((string Member, string Session) session) =>
    new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, session.Member), new("hbr-session", session.Session)], CookieAuthenticationDefaults.AuthenticationScheme));
static IResult Conflict(string message) => Results.Conflict(new { error = message });

internal sealed record RegularApproval(string SteamId);
internal sealed record GameChoice(uint AppId);
internal sealed record Moderation(string DeviceId);

internal sealed record MemberKeyInput(string? Key);
internal sealed record MemberAiPreview(string? Message);
