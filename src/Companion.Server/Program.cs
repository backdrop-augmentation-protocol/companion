using System.Net.WebSockets;
using System.Text.Json;
using Bap.Companion.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new CompanionOptions());
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<IWorkshopComponentSource, EmptyWorkshopComponentSource>();
builder.Services.AddSingleton<IConfigStore>(_ => new JsonConfigStore(Path.Combine(AppContext.BaseDirectory, "data", "config.json")));
var app = builder.Build();
app.UseWebSockets();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapPost("/v1/session", (SessionRequest request, ISessionStore sessions, CompanionOptions options) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientName) || string.IsNullOrWhiteSpace(request.ClientKind)) return Results.BadRequest(new { error = "clientName and clientKind are required" });
    var session = sessions.Create(request.ClientKind, options.SessionLifetime);
    return Results.Ok(new SessionResponse(session.Id, session.Token, session.ExpiresAt));
});

app.MapGet("/v1/components", async (HttpRequest request, IWorkshopComponentSource source, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, async () => Results.Ok((await source.DiscoverAsync(ct)).Where(IsValid).Select(x => x.Manifest))));
app.MapGet("/v1/components/{id}/manifest", async (string id, HttpRequest request, IWorkshopComponentSource source, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, async () =>
    {
        var item = await source.GetAsync(id, ct);
        return item is null ? Results.NotFound() : IsValid(item) ? Results.Ok(item.Manifest) : Results.Problem("component manifest is invalid", statusCode: 422);
    }));

app.MapGet("/v1/config", async (HttpRequest request, IConfigStore store, ISessionStore sessions, CancellationToken ct) => await Authenticated(request, sessions, async () => Results.Ok(await store.ReadAsync(ct))));
app.MapPut("/v1/config/global", async (HttpRequest request, Dictionary<string, object?> values, IConfigStore store, ISessionStore sessions, CancellationToken ct) => await Authenticated(request, sessions, async () => { await store.WriteGlobalAsync(values, ct); return Results.NoContent(); }));
app.MapPut("/v1/config/wallpapers/{wallpaperId}", async (string wallpaperId, HttpRequest request, Dictionary<string, object?> values, IConfigStore store, ISessionStore sessions, CancellationToken ct) => await Authenticated(request, sessions, async () => { await store.WriteWallpaperAsync(wallpaperId, values, ct); return Results.NoContent(); }));

app.Map("/v1/ws", async (HttpContext context, ISessionStore sessions, CompanionOptions options) =>
{
    var token = context.Request.Query["token"].FirstOrDefault();
    if (token is null || !sessions.TryGet(token, out var session)) { context.Response.StatusCode = 401; return; }
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[16 * 1024];
    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
    if (result.MessageType != WebSocketMessageType.Text) { await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text handshake required", context.RequestAborted); return; }
    HostHello? hello;
    try { hello = JsonSerializer.Deserialize<HostHello>(buffer.AsSpan(0, result.Count), json); } catch { hello = null; }
    if (hello is null || string.IsNullOrWhiteSpace(hello.HostId) || hello.ProtocolVersion != options.ProtocolVersion)
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid host handshake", context.RequestAborted); return;
    }
    var reply = JsonSerializer.SerializeToUtf8Bytes(new ProtocolMessage("companion.hello", null, new CompanionHello(options.ProtocolVersion, options.CompanionVersion, session.Id, ["component.discovery", "config"])), json);
    await socket.SendAsync(reply, WebSocketMessageType.Text, true, context.RequestAborted);
    while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
    {
        result = await socket.ReceiveAsync(buffer, context.RequestAborted);
        if (result.MessageType == WebSocketMessageType.Close) break;
    }
});

app.Run();

static bool IsValid(ComponentDescriptor item) => ComponentManifestValidator.Validate(item.Manifest).Count == 0;
static async Task<IResult> Authenticated(HttpRequest request, ISessionStore sessions, Func<Task<IResult>> action)
{
    var header = request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    return token is not null && sessions.TryGet(token, out _) ? await action() : Results.Unauthorized();
}

public partial class Program;
