using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using HourBoostr.Remastered;
using Microsoft.AspNetCore.RateLimiting;

try
{
    var options = LaunchOptions.Parse(args);
    var store = new DataStore(options.DataDirectory);
    var vault = new SecretVault(store);
    if (args.FirstOrDefault() == "ai-key")
    {
        var key = args.Contains("--remove") ? "" : Console.ReadLine();
        if (key is null || (!args.Contains("--remove") && string.IsNullOrWhiteSpace(key))) throw new ArgumentException("Supply a Groq key on standard input, or use --remove.");
        AiReplyService.For(store).SaveKey(key);
        Console.WriteLine(args.Contains("--remove") ? "Backend Groq key removed." : "Backend Groq key saved. No restart required.");
        return 0;
    }
    if (await Cli.ExecuteAsync(args, options, store, vault) is int code) return code;
    using var instance = new FileStream(store.Resolve("instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    if (options.ProtectedNames.Length > 0) store.ProtectAccounts(options.ProtectedNames);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Host.UseWindowsService(settings => settings.ServiceName = "HourBoostr Remastered by Otter");
    builder.Host.UseSystemd();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]" + (options.PublicHost is null ? "" : ";" + options.PublicHost);
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Limits.MaxRequestBodySize = 256 * 1024;
        kestrel.Listen(IPAddress.Parse(options.Listen), options.Port, listen =>
        { if (options.Remote) listen.UseHttps(options.Certificate!, options.CertificatePassword); });
    });
    builder.Services.ConfigureHttpJsonOptions(settings =>
    {
        settings.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        settings.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });
    builder.Services.AddSingleton(store); builder.Services.AddSingleton(vault);
    builder.Services.AddSingleton<EventLog>(); builder.Services.AddSingleton<DashboardSecurity>();
    builder.Services.AddSingleton(provider => new SteamEngine(store, vault, provider.GetRequiredService<EventLog>(), store.Settings.ProtectedLoginNames));
    builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamEngine>());
    builder.Services.AddRateLimiter(limits =>
    {
        limits.RejectionStatusCode = 429;
        limits.AddFixedWindowLimiter("signin", limiter => { limiter.PermitLimit = 8; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; limiter.AutoReplenishment = true; });
    });
    var app = builder.Build(); var security = app.Services.GetRequiredService<DashboardSecurity>();
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        try
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "no-store";
                var origin = context.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
                if (context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers["X-HBR-Request"] != "1") { context.Response.StatusCode = 403; return; }
                var bearer = context.Request.Headers.Authorization.ToString();
                var authenticated = bearer.StartsWith("Bearer ", StringComparison.Ordinal) && security.Matches(bearer[7..]) || security.ValidateCookie(context.Request.Cookies["hbr-session"], DateTimeOffset.UtcNow);
                if (context.Request.Path != "/api/session" && !authenticated) { context.Response.StatusCode = 401; return; }
            }
            await next();
        }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = error switch { KeyNotFoundException => 404, ArgumentException or JsonException or BadHttpRequestException => 400, HttpRequestException => 502, _ => 500 };
            await context.Response.WriteAsJsonAsync(new { error = error is ArgumentException or KeyNotFoundException ? error.Message : "The request could not be completed (" + error.GetType().Name + ")." });
        }
    });
    app.UseRateLimiter(); app.UseDefaultFiles(); app.UseStaticFiles();
    app.MapGet("/health", () => new { status = "running", version = ReleaseTools.CurrentVersion });
    app.MapPost("/api/session", (PairRequest request, HttpContext context) =>
    {
        if (!security.Matches(request.Key)) return Results.Unauthorized();
        context.Response.Cookies.Append("hbr-session", security.CreateCookie(DateTimeOffset.UtcNow), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = options.Remote, MaxAge = TimeSpan.FromDays(30), IsEssential = true, Path = "/" });
        return Results.Ok(new { paired = true });
    }).RequireRateLimiting("signin");
    app.MapDelete("/api/session", (HttpContext context) => { context.Response.Cookies.Delete("hbr-session"); return Results.Ok(); });
    app.MapGet("/api/state", (SteamEngine engine, EventLog events) => new { version = ReleaseTools.CurrentVersion, startedAt = engine.StartedAt, now = DateTimeOffset.UtcNow, accounts = engine.Snapshot(), events = events.Entries().Take(30), remote = options.Remote });
    app.MapGet("/api/ai/status", () => AiReplyService.For(store).Status());
    app.MapPost("/api/ai/preview", async (AiPreviewRequest request, HttpContext context) =>
    {
        var account = store.Account(request.AccountId) ?? throw new KeyNotFoundException("Account not found.");
        if (request.Message is null || request.Message.Length is < 1 or > 1000) throw new ArgumentException("Preview message must be 1–1000 characters.");
        var reply = await AiReplyService.For(store).GenerateAsync(account.Id, account.AiReplies, "preview", request.Message, context.RequestAborted);
        return Results.Ok(new { reply, details = AiReplyService.For(store).Status() });
    }).RequireRateLimiting("signin");
    app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
    {
        _ = Task.Run(async () => { await Task.Delay(200); lifetime.StopApplication(); });
        return Results.Accepted();
    });
    app.MapPost("/api/accounts", (CreateAccountRequest request) => Results.Ok(store.Add(request.Label)));
    app.MapGet("/api/accounts/{id}/settings", (string id) => Results.Ok(store.Account(id) ?? throw new KeyNotFoundException("Account not found.")));
    app.MapGet("/api/accounts/{id}/profile-comments", (string id, SteamEngine engine) =>
        Results.Ok(engine.RegularsSession(id).ReadProfileComments()));
    app.MapPut("/api/accounts/{id}/profile-comments", (string id, SteamEngine engine, ProfileCommentSettings request) =>
    {
        engine.RegularsSession(id).ConfigureProfileComments(request.Enabled);
        return Results.NoContent();
    });
    app.MapPost("/api/accounts/{id}/profile-comments", (string id, SteamEngine engine, ProfileCommentDismissal request) =>
    {
        if (!ulong.TryParse(request.Id, out var commentId) || commentId == 0) throw new ArgumentException("Invalid comment ID.");
        engine.RegularsSession(id).DismissProfileComment(request.Id);
        return Results.NoContent();
    });
    app.MapGet("/api/accounts/{id}/group-regulars", async (string id, SteamEngine engine, CancellationToken token) =>
        Results.Ok(await engine.RegularsSession(id).ReadRegularsAsync(token)));
    app.MapPut("/api/accounts/{id}/group-regulars", (string id, SteamEngine engine, RegularSettings request) =>
    {
        engine.RegularsSession(id).ConfigureRegulars(request);
        return Results.NoContent();
    });
    app.MapPost("/api/accounts/{id}/group-regulars", (string id, SteamEngine engine, RegularAction request) =>
    {
        engine.RegularsSession(id).ActOnRegular(request);
        return Results.NoContent();
    });
    app.MapPut("/api/accounts/{id}/settings", (string id, AccountSettings request) =>
    {
        var current = store.Account(id) ?? throw new KeyNotFoundException("Account not found.");
        request.Id = id; request.LoginName = current.LoginName;
        request.PresenceBadge ??= current.PresenceBadge;
        request.Validate();
        var display = request.ProfileDisplay; var previous = current.ProfileDisplay;
        display.AnchorUtc = display.Mode != previous.Mode || display.IntervalMinutes != previous.IntervalMinutes || display.CustomText != previous.CustomText || !display.AppIds.SequenceEqual(previous.AppIds)
            ? DateTimeOffset.UtcNow : previous.AnchorUtc;
        store.Save(request); return Results.Ok(store.Account(id));
    });
    app.MapPost("/api/accounts/{id}/login", async (string id, LoginRequest request, SteamEngine engine) => { await engine.LoginAsync(id, request); return Results.Accepted(); }).RequireRateLimiting("signin");
    app.MapPost("/api/accounts/{id}/guard", (string id, GuardRequest request, SteamEngine engine) => engine.SubmitGuard(id, request.Code) ? Results.Ok() : Results.BadRequest(new { error = "There is no pending Steam Guard prompt." })).RequireRateLimiting("signin");
    app.MapPost("/api/accounts/{id}/pause", (string id, SteamEngine engine) => { engine.Pause(id, true); return Results.Ok(); });
    app.MapPost("/api/accounts/{id}/resume", (string id, SteamEngine engine) => { engine.Pause(id, false); return Results.Ok(); });
    app.MapPost("/api/accounts/{id}/randomize", (string id, SteamEngine engine) =>
    {
        var config = store.Account(id) ?? throw new KeyNotFoundException("Account not found.");
        config.SelectionMode = "random"; store.Save(config); engine.Reselect(id); return Results.Ok();
    });
    app.MapPost("/api/accounts/{id}/refresh", async (string id, SteamEngine engine) => { await engine.RefreshAsync(id); return Results.Ok(); });
    app.MapPost("/api/accounts/{id}/forget", async (string id, SteamEngine engine) => { await engine.ForgetAsync(id); return Results.Ok(); });
    app.MapDelete("/api/accounts/{id}", async (string id, SteamEngine engine) => { await engine.RemoveAsync(id); return Results.Ok(); });
    app.MapGet("/api/accounts/{id}/library", (string id, string? search, int? offset, int? limit, SteamEngine engine) =>
    {
        if (store.Account(id) is null) throw new KeyNotFoundException("Account not found.");
        var stats = engine.Statistics(id);
        var games = stats.Games.Values.Where(x => string.IsNullOrEmpty(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.AppId.ToString() == search).OrderBy(x => x.Name).ToList();
        return new { total = games.Count, games = games.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 100)).ToArray() };
    });
    app.MapGet("/api/accounts/{id}/statistics", (string id, SteamEngine engine) => Results.Ok(engine.Statistics(id)));
    app.MapGet("/api/timezones", () => TimeZoneInfo.GetSystemTimeZones().Select(x => new { id = x.Id, label = x.DisplayName }));
    app.MapGet("/api/diagnostics", (SteamEngine engine) => Results.Ok(Diagnostics.Report(store, engine)));
    app.MapGet("/api/updates", async () => Results.Ok(await ReleaseTools.CheckAsync(store.Settings.ReleaseRepository)));
    app.MapPut("/api/release-repository", (JsonElement request) =>
    {
        var repository = request.GetProperty("repository").GetString();
        if (!string.IsNullOrWhiteSpace(repository) && !System.Text.RegularExpressions.Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("Use owner/repository.");
        // Only the release source is changed; account configuration remains in the same file.
        store.SetReleaseRepository(repository);
        return Results.Ok();
    });
    Console.WriteLine($"HourBoostr Remastered by Otter {ReleaseTools.CurrentVersion}\nDashboard: {options.BaseUrl}\nRun 'open' with the same --data-dir to pair this browser.\nNo Steam password is saved. Use a separate account for local tests.");
    await app.RunAsync(); return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is ArgumentException ? error.Message : "Startup failed: " + error.GetType().Name + ". Run doctor for environment diagnostics.");
    return 1;
}

public partial class Program { }
