param(
    [string]$BaseUrl = "http://127.0.0.1:4311",
    [string]$WallpaperId = "bap-host-wallpaper",
    [string]$AuthorizationPipeName = "Bap.Companion.E2E"
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')
Add-Type -AssemblyName System.Net.Http

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Name) {
    if ($Actual -ne $Expected) { throw "$Name expected '$Expected' but was '$Actual'" }
}

function Invoke-AuthorizationPipe([hashtable]$Request) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $AuthorizationPipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(3000)
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 1024, $true)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8, 1024, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine(($Request | ConvertTo-Json -Depth 8 -Compress))
        $responseLine = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($responseLine)) { throw "Authorization pipe returned no response." }
        return $responseLine | ConvertFrom-Json
    }
    finally { $pipe.Dispose() }
}

function Wait-ForPendingApproval([string]$SessionId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $snapshot = Invoke-AuthorizationPipe @{ operation = 'snapshot' }
        $request = @($snapshot.requests | Where-Object { $_.sessionId -eq $SessionId } | Select-Object -First 1)
        if ($request.Count -gt 0) { return $request[0] }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Host approval request $SessionId did not arrive on the authorization pipe '$AuthorizationPipeName'. Start the Server with BAP_AUTHORIZATION_PIPE set to this name."
}

function New-HostHello([string]$Id) {
    return @{
        protocol = @{ name = "bap"; version = "0.1.0"; profile = "core" }
        kind = "request"
        id = $Id
        timestamp = [DateTimeOffset]::UtcNow.ToString("o")
        method = "hello"
        payload = @{
            role = "host"
            identity = @{ id = "org.bap.examples.host-wallpaper"; name = "BAP Host Wallpaper Demo"; version = "0.1.0" }
            protocolVersions = @("0.1.0")
            capabilities = @("component-discovery", "config")
            requestedPermissions = @("component.discovery", "config.read", "config.write")
        }
    } | ConvertTo-Json -Depth 8
}

function Send-HostHello($Socket, [string]$Id) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((New-HostHello $Id))
    $Socket.SendAsync($bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
}

function Receive-JsonMessage($Socket) {
    $buffer = New-Object byte[] 8192
    $received = $Socket.ReceiveAsync($buffer, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw "Expected a JSON WebSocket message, received $($received.MessageType)." }
    return [Text.Encoding]::UTF8.GetString($buffer, 0, $received.Count) | ConvertFrom-Json
}

function New-HostWebSocket([string]$AccessToken) {
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $uri = [Uri]("ws://$(([Uri]$BaseUrl).Authority)/v1/ws?token=" + [Uri]::EscapeDataString($AccessToken))
    $socket.ConnectAsync($uri, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    return $socket
}

function Assert-Unauthorized([string]$AccessToken, [string]$Name) {
    try {
        Invoke-RestMethod "$BaseUrl/v1/components" -Headers @{ Authorization = "Bearer $AccessToken" } | Out-Null
        throw "$Name unexpectedly retained component discovery access after Host revocation"
    }
    catch {
        $statusCode = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($statusCode -ne 401) { throw }
    }
}

$health = Invoke-RestMethod "$BaseUrl/healthz"
Assert-Equal $health.status "ok" "health status"
Assert-Equal $health.protocol.name "bap" "health protocol"

$sessionBody = @{
    clientName = "BAP Companion E2E"
    clientKind = "host"
    protocol = @{ name = "bap"; version = "0.1.0"; profile = "core" }
} | ConvertTo-Json -Depth 5
$session = Invoke-RestMethod "$BaseUrl/v1/session" -Method Post -ContentType "application/json" -Body $sessionBody
if ([string]::IsNullOrWhiteSpace($session.accessToken)) { throw "session did not return an access token" }
$headers = @{ Authorization = "Bearer $($session.accessToken)" }

try {
    Invoke-RestMethod "$BaseUrl/v1/components" -Headers $headers | Out-Null
    throw "unapproved session unexpectedly accessed component discovery"
}
catch {
    $statusCode = [int]$_.Exception.Response.StatusCode
    if ($statusCode -ne 403) { throw }
}

$ws = New-HostWebSocket $session.accessToken
Send-HostHello $ws "companion-e2e-hello-1"
$pending = Wait-ForPendingApproval $session.sessionId
$approved = Invoke-AuthorizationPipe @{
    operation = 'approve'
    sessionId = $pending.sessionId
    permissions = @('component.discovery', 'config.read', 'config.write')
    remember = $true
}
if (-not $approved.success) { throw "Host approval decision could not be applied" }
$response = Receive-JsonMessage $ws
if (-not $response.ok) { throw "Companion WebSocket hello was rejected" }
Assert-Equal $response.result.identity.id "org.bap.companion" "companion identity"
Assert-Equal $response.result.role "companion" "companion role"
Assert-Equal $response.result.selectedProtocol.version "0.1.0" "selected protocol"
Assert-Equal $response.sessionId $session.sessionId "session id"
if ($response.result.permissions.Count -lt 2) { throw "Companion did not grant expected permissions" }
$components = Invoke-RestMethod "$BaseUrl/v1/components" -Headers $headers
$before = Invoke-RestMethod "$BaseUrl/v1/config" -Headers $headers
$values = @{ locale = "zh-CN"; showSeconds = $false }
$payload = $values | ConvertTo-Json -Depth 5
$http = [System.Net.Http.HttpClient]::new()
$request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, "$BaseUrl/v1/config/wallpapers/$WallpaperId")
$request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $session.accessToken)
$request.Content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, "application/json")
$putResponse = $http.SendAsync($request).GetAwaiter().GetResult()
if (-not $putResponse.IsSuccessStatusCode) { throw "wallpaper config update failed with HTTP $([int]$putResponse.StatusCode)" }
$request.Dispose(); $putResponse.Dispose(); $http.Dispose()
$after = Invoke-RestMethod "$BaseUrl/v1/config" -Headers $headers
Assert-Equal $after.wallpapers.$WallpaperId.locale "zh-CN" "wallpaper locale"
Assert-Equal ([bool]$after.wallpapers.$WallpaperId.showSeconds) $false "wallpaper seconds setting"
$ws.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "E2E complete", [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
$ws.Dispose()

# A remembered Host ID is not proof of identity: every new session must wait for
# an explicit approval, and revoking the Host must invalidate all its sessions.
$secondSession = Invoke-RestMethod "$BaseUrl/v1/session" -Method Post -ContentType "application/json" -Body $sessionBody
$secondHeaders = @{ Authorization = "Bearer $($secondSession.accessToken)" }
try {
    Invoke-RestMethod "$BaseUrl/v1/components" -Headers $secondHeaders | Out-Null
    throw "remembered Host unexpectedly accessed component discovery before reapproval"
}
catch {
    $statusCode = [int]$_.Exception.Response.StatusCode
    if ($statusCode -ne 403) { throw }
}
$ws2 = New-HostWebSocket $secondSession.accessToken
Send-HostHello $ws2 "companion-e2e-hello-2"
$pending2 = Wait-ForPendingApproval $secondSession.sessionId
$approved2 = Invoke-AuthorizationPipe @{
    operation = 'approve'
    sessionId = $pending2.sessionId
    permissions = @('component.discovery', 'config.read', 'config.write')
    remember = $false
}
if (-not $approved2.success) { throw "Second Host approval decision could not be applied" }
$response2 = Receive-JsonMessage $ws2
if (-not $response2.ok) { throw "Second Companion WebSocket hello was rejected" }

$revoked = Invoke-AuthorizationPipe @{ operation = 'revoke'; sessionId = 'org.bap.examples.host-wallpaper' }
if (-not $revoked.success) { throw "Host trust revocation was not applied" }
$closeBuffer = New-Object byte[] 256
$closeTimeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
try { $closeResult = $ws2.ReceiveAsync($closeBuffer, $closeTimeout.Token).GetAwaiter().GetResult() }
finally { $closeTimeout.Dispose() }
if ($closeResult.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Close) { throw "Revocation did not close the active Host WebSocket" }
if ($closeResult.CloseStatus -ne [Net.WebSockets.WebSocketCloseStatus]::PolicyViolation) { throw "Revocation closed the WebSocket with unexpected status '$($closeResult.CloseStatus)'" }
$ws2.Dispose()
Assert-Unauthorized $session.accessToken "Previously approved session"
Assert-Unauthorized $secondSession.accessToken "Reapproved session"

[pscustomobject]@{
    status = "passed"
    sessionId = $session.sessionId
    discoveredComponents = @($components.value).Count
    wallpaperId = $WallpaperId
    websocketHello = $true
    grantedPermissions = @($response.result.permissions)
    savedHostRequiresReapproval = $true
    revokeClosesWebSocket = $true
    revokedTokensUnauthorized = $true
} | ConvertTo-Json -Depth 6
