using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bap.Companion.Core;
using Bap.Companion.Server;

var builder = WebApplication.CreateBuilder(args);
// The browser WebSocket carries a short-lived bearer token in its URL.
// Do not write full request URLs, including that token, to routine logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddSingleton(new CompanionOptions());
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<HostAuthorizationQueue>();
builder.Services.AddHostedService<AuthorizationPipeServer>();
builder.Services.AddSingleton(_ => new JsonPresetStore(JsonPresetStore.DefaultPath));
builder.Services.AddSingleton(_ => InstalledCatalog.FromEnvironment());
builder.Services.AddSingleton<IWorkshopComponentSource>(_ => new WorkshopDirectorySource(() => WorkshopRoots.FromEnvironment()));
builder.Services.AddSingleton<IConfigStore>(_ => new JsonConfigStore(Path.Combine(AppContext.BaseDirectory, "data", "config.json")));
builder.Services.AddCors(options => options.AddPolicy("local-companion", policy => policy
    .SetIsOriginAllowed(IsAllowedOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()));
var app = builder.Build();
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
    var host = context.Request.Host.Host;
    if (remote is null || !IPAddress.IsLoopback(remote) ||
        host != "127.0.0.1" && host != "::1" && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});
app.UseWebSockets();
app.UseCors("local-companion");

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", protocol = new { name = "bap", version = "0.1.0", profile = "core" } }));

app.MapPost("/v1/session", (SessionRequest? request, ISessionStore sessions, CompanionOptions options) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.ClientName) || string.IsNullOrWhiteSpace(request.ClientKind))
        return Error(StatusCodes.Status400BadRequest, "INVALID_MESSAGE", "clientName and clientKind are required");
    if (request.Protocol is not null && !IsCompatible(request.Protocol, options.Protocol))
        return Error(StatusCodes.Status400BadRequest, "UNSUPPORTED_VERSION", "requested protocol is not supported");
    var session = sessions.Create(request.ClientKind, options.SessionLifetime);
    return Results.Ok(new SessionResponse(session.Id, session.Token, session.ExpiresAt));
});

app.MapGet("/v1/components", async (HttpRequest request, IWorkshopComponentSource source, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, "component.discovery", async () => Results.Ok((await source.DiscoverAsync(ct)).Where(IsValid).Select(x => x.Manifest))));

app.MapGet("/v1/hosts/{hostId}/components", async (string hostId, HttpRequest request, ISessionStore sessions,
    JsonPresetStore presets, InstalledCatalog catalog, CancellationToken ct) =>
    await Authenticated(request, sessions, "component.discovery", async () => Results.Ok(EffectiveComponents.Resolve(
        await presets.ReadAsync(ct), hostId, await catalog.DiscoverAsync(ct)))));

app.MapGet("/v1/catalog/components/{key}/resources/{**resourcePath}", async (string key, string? resourcePath,
    HttpContext context, ISessionStore sessions, InstalledCatalog catalog, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return await Authenticated(context.Request, sessions, "component.discovery", async () =>
    {
        var item = await catalog.GetAsync(key, ct);
        if (item?.ComponentManifest is null) return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "component was not found");
        if (!WorkshopResourceResolver.TryResolve(new(item.ComponentManifest, item.ProjectDirectory), resourcePath, out var path))
            return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "resource was not found");
        return Results.File(path, ContentType(path), enableRangeProcessing: true);
    });
});

app.MapGet("/v1/components/{id}/manifest", async (string id, HttpRequest request, IWorkshopComponentSource source, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, "component.discovery", async () =>
    {
        var item = await source.GetAsync(id, ct);
        if (item is null) return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "component was not found");
        return IsValid(item) ? Results.Ok(item.Manifest) : Error(StatusCodes.Status422UnprocessableEntity, "INVALID_MESSAGE", "component manifest is invalid");
    }));

app.MapGet("/v1/components/{id}/resources/{**resourcePath}", async (string id, string? resourcePath, HttpContext context, IWorkshopComponentSource source, ISessionStore sessions, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return await Authenticated(context.Request, sessions, "component.discovery", async () =>
    {
        var component = await source.GetAsync(id, ct);
        if (component is null) return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "component was not found");
        if (!WorkshopResourceResolver.TryResolve(component, resourcePath, out var path)) return Error(StatusCodes.Status404NotFound, "NOT_FOUND", "resource was not found");
        return Results.File(path, ContentType(path), enableRangeProcessing: true);
    });
});

app.MapGet("/v1/config", async (HttpRequest request, IConfigStore store, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, "config.read", async () => Results.Ok(await store.ReadAsync(ct))));

app.MapPut("/v1/config/global", async (HttpRequest request, Dictionary<string, object?>? values, IConfigStore store, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, "config.write", async () =>
    {
        if (values is null) return Error(StatusCodes.Status400BadRequest, "INVALID_MESSAGE", "configuration object is required");
        await store.WriteGlobalAsync(values, ct);
        return Results.NoContent();
    }));

app.MapPut("/v1/config/wallpapers/{wallpaperId}", async (string wallpaperId, HttpRequest request, Dictionary<string, object?>? values, IConfigStore store, ISessionStore sessions, CancellationToken ct) =>
    await Authenticated(request, sessions, "config.write", async () =>
    {
        if (string.IsNullOrWhiteSpace(wallpaperId) || values is null) return Error(StatusCodes.Status400BadRequest, "INVALID_MESSAGE", "wallpaperId and configuration object are required");
        await store.WriteWallpaperAsync(wallpaperId, values, ct);
        return Results.NoContent();
    }));

app.Map("/v1/ws", async (HttpContext context, ISessionStore sessions, CompanionOptions options, HostAuthorizationQueue authorizations, ILogger<Program> logger) =>
{
    var token = GetToken(context.Request, allowQueryToken: true);
    if (token is null || !sessions.TryGet(token, out var session)) { context.Response.StatusCode = 401; return; }
    using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.RevocationToken);
    var sessionCancellation = sessionLifetime.Token;
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!IsAllowedOrigin(context.Request.Headers.Origin)) { context.Response.StatusCode = 403; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var permissionsGranted = false;
    try
    {
        var raw = await ReceiveTextAsync(socket, sessionCancellation);
        if (raw is null || !TryReadMessage(raw, json, out var hello) || hello is null || hello.Kind != "request" || hello.Method != "hello" || string.IsNullOrWhiteSpace(hello.Id))
        { await CloseProtocolError(socket, sessionCancellation); return; }
        var payload = hello.Payload?.Deserialize<HostHelloPayload>(json);
        if (payload is null || payload.Identity is null ||
            string.IsNullOrWhiteSpace(payload.Identity.Id) || payload.Identity.Id.Length > 256 ||
            string.IsNullOrWhiteSpace(payload.Identity.Name) || payload.Identity.Name.Length > 256 ||
            string.IsNullOrWhiteSpace(payload.Identity.Version) || payload.Identity.Version.Length > 128 ||
            payload.ProtocolVersions is null || payload.ProtocolVersions.Count > 16 ||
            payload.Capabilities?.Count > 32 || payload.RequestedPermissions?.Count > 32)
        {
            await SendAsync(socket, ErrorResponse(options.Protocol, hello.Id, session.Id, "INVALID_MESSAGE", "host hello fields are invalid"), sessionCancellation);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid Host hello", sessionCancellation);
            return;
        }
        if (payload.Role != "host" || !IsCompatible(hello.Protocol, options.Protocol) ||
            !payload.ProtocolVersions.Contains(options.Protocol.Version, StringComparer.Ordinal))
        {
            await SendAsync(socket, ErrorResponse(options.Protocol, hello.Id, session.Id, "UNSUPPORTED_VERSION", "host hello is not compatible"), sessionCancellation);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unsupported protocol", sessionCancellation); return;
        }
        var capabilities = (payload.Capabilities ?? []).Where(c => c is "component-discovery" or "config").Distinct().ToArray();
        var requestedPermissions = (payload.RequestedPermissions ?? []).Where(p =>
            p == "component.discovery" && capabilities.Contains("component-discovery") ||
            p is "config.read" or "config.write" && capabilities.Contains("config")).Distinct().ToArray();
        var pendingRequest = new HostAuthorizationRequest(
            session.Id, payload.Identity, capabilities, requestedPermissions, DateTimeOffset.UtcNow);
        if (!sessions.BindHost(token, payload.Identity.Id))
        { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "session expired", context.RequestAborted); return; }
        logger.LogInformation(
            "Host session {SessionId} requested access for self-reported identity {HostId} ({HostName}); awaiting approval",
            session.Id, payload.Identity.Id, payload.Identity.Name);
        authorizations.TryEnqueue(pendingRequest, out var decisionTask);
        HostAuthorizationDecision decision;
        using (var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation))
        {
            approvalTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { decision = await decisionTask.WaitAsync(approvalTimeout.Token); }
            catch (OperationCanceledException)
            {
                authorizations.Cancel(session.Id);
                if (!context.RequestAborted.IsCancellationRequested)
                    await SendAsync(socket, ErrorResponse(options.Protocol, hello.Id, session.Id, AuthorizationErrorCodes.TimedOut, "Host approval was not completed in time"), sessionCancellation);
                return;
            }
        }
        if (!decision.Approved)
        {
            await SendAsync(socket, ErrorResponse(options.Protocol, hello.Id, session.Id, AuthorizationErrorCodes.Denied, "Host access was denied in Companion"), sessionCancellation);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Host approval denied", sessionCancellation);
            return;
        }
        var permissions = decision.GrantedPermissions;
        var grantedCapabilities = capabilities.Where(capability =>
            capability == "component-discovery" && permissions.Contains("component.discovery", StringComparer.Ordinal) ||
            capability == "config" && permissions.Any(permission => permission is "config.read" or "config.write"))
            .ToArray();
        if (!sessions.SetPermissions(token, permissions))
        { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "session expired", sessionCancellation); return; }
        permissionsGranted = true;
        var result = new CompanionHelloPayload(options.Protocol, "companion", new(options.CompanionId, options.CompanionName, options.CompanionVersion), grantedCapabilities, permissions);
        await SendAsync(socket, Response(options.Protocol, hello.Id, session.Id, result), sessionCancellation);
        logger.LogInformation("Host session {SessionId} approved; Companion hello response sent with {PermissionCount} permissions", session.Id, permissions.Count);
        while (socket.State == WebSocketState.Open && !sessionCancellation.IsCancellationRequested)
        {
            // Keep revocation independent from the pending receive. Cancelling a
            // WebSocket receive can abort the transport before a close frame is sent.
            var receiveTask = ReceiveTextAsync(socket, context.RequestAborted);
            var revocationTask = Task.Delay(Timeout.InfiniteTimeSpan, session.RevocationToken);
            if (await Task.WhenAny(receiveTask, revocationTask) != receiveTask)
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Host authorization revoked", CancellationToken.None);
                return;
            }
            var message = await receiveTask;
            if (message is null) break;
            if (!TryReadMessage(message, json, out var request) || request is null || request.Kind != "request" || string.IsNullOrWhiteSpace(request.Id))
            { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "valid BAP request required", sessionCancellation); return; }
            if (request.SessionId != session.Id)
            { await SendAsync(socket, ErrorResponse(options.Protocol, request.Id, session.Id, "UNAUTHORIZED", "session identifier does not match"), sessionCancellation); continue; }
            await SendAsync(socket, ErrorResponse(options.Protocol, request.Id, session.Id, "NOT_FOUND", $"method '{request.Method ?? ""}' is not supported"), sessionCancellation);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        logger.LogDebug("Host WebSocket request aborted for session {SessionId}", session.Id);
    }
    catch (OperationCanceledException) when (session.RevocationToken.IsCancellationRequested)
    {
        logger.LogInformation("Host authorization revoked for session {SessionId}", session.Id);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Host authorization revoked", CancellationToken.None);
    }
    catch (WebSocketException error)
    {
        logger.LogWarning(error, "Host WebSocket failed for session {SessionId}", session.Id);
    }
    finally
    {
        if (permissionsGranted) sessions.ClearPermissions(token);
        // Disconnects never restore lifecycle state or permissions; reconnects must handshake again.
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnected", CancellationToken.None);
        logger.LogInformation(
            "Host WebSocket ended for session {SessionId}: state={State}, closeStatus={CloseStatus}, closeDescription={CloseDescription}",
            session.Id, socket.State, socket.CloseStatus, socket.CloseStatusDescription);
    }
});

app.Run();

static bool IsValid(ComponentDescriptor item) => ComponentManifestValidator.Validate(item.Manifest).Count == 0;
static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".html" => "text/html; charset=utf-8",
    ".css" => "text/css; charset=utf-8",
    ".js" => "text/javascript; charset=utf-8",
    ".json" => "application/json; charset=utf-8",
    ".svg" => "image/svg+xml",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".webp" => "image/webp",
    ".gif" => "image/gif",
    ".mp3" => "audio/mpeg",
    ".wav" => "audio/wav",
    ".mp4" => "video/mp4",
    ".webm" => "video/webm",
    _ => "application/octet-stream"
};
static string? GetToken(HttpRequest request, bool allowQueryToken = false)
{
    var header = request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
    return allowQueryToken ? request.Query["token"].FirstOrDefault() : null;
}
static bool IsAllowedOrigin(string? origin) => string.IsNullOrWhiteSpace(origin) || origin is "null" or "file://" || Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "localhost" or "127.0.0.1" or "[::1]";
static bool IsCompatible(ProtocolDescriptor requested, ProtocolDescriptor supported) => requested.Name == supported.Name && requested.Profile == supported.Profile && requested.Version == supported.Version;
static async Task<IResult> Authenticated(HttpRequest request, ISessionStore sessions, string requiredPermission, Func<Task<IResult>> action)
{
    var token = GetToken(request);
    if (token is null || !sessions.TryGet(token, out var session)) return Error(401, "UNAUTHORIZED", "a valid bearer session is required");
    if (!session.GrantedPermissions.Contains(requiredPermission)) return Error(403, "CAPABILITY_DENIED", "the session has not negotiated the required permission");
    return await action();
}
static IResult Error(int status, string code, string message, bool retryable = false) => Results.Json(new BapError(code, message, retryable), statusCode: status);
static ProtocolMessage Response(ProtocolDescriptor protocol, string id, string sessionId, object result) => new(protocol, "response", $"response-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, sessionId, id, Ok: true, Result: JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
static ProtocolMessage ErrorResponse(ProtocolDescriptor protocol, string correlationId, string sessionId, string code, string message) => new(protocol, "response", $"response-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, sessionId, correlationId, Ok: false, Error: new BapError(code, message));
static bool TryReadMessage(string raw, JsonSerializerOptions json, out ProtocolMessage? message)
{
    try { message = JsonSerializer.Deserialize<ProtocolMessage>(raw, json); return message is not null; }
    catch (JsonException) { message = null; return false; }
}
static async Task SendAsync(WebSocket socket, ProtocolMessage message, CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}
static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
{
    using var buffer = new MemoryStream(); var chunk = new byte[16 * 1024]; WebSocketReceiveResult result;
    do { result = await socket.ReceiveAsync(chunk, cancellationToken); if (result.MessageType == WebSocketMessageType.Close) return null; if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("text message required"); buffer.Write(chunk, 0, result.Count); } while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(buffer.ToArray());
}
static Task CloseProtocolError(WebSocket socket, CancellationToken cancellationToken) => socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "valid BAP hello request required", cancellationToken);
public partial class Program;
