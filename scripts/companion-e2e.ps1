param(
    [string]$BaseUrl = "http://127.0.0.1:4311",
    [string]$WallpaperId = "bap-host-wallpaper"
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')
Add-Type -AssemblyName System.Net.Http

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Name) {
    if ($Actual -ne $Expected) { throw "$Name expected '$Expected' but was '$Actual'" }
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

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$wsUri = [Uri]("ws://$(([Uri]$BaseUrl).Authority)/v1/ws?token=" + [Uri]::EscapeDataString($session.accessToken))
$ws.ConnectAsync($wsUri, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$hello = @{
    protocol = @{ name = "bap"; version = "0.1.0"; profile = "core" }
    kind = "request"
    id = "companion-e2e-hello"
    timestamp = [DateTimeOffset]::UtcNow.ToString("o")
    method = "hello"
    payload = @{
        role = "host"
        identity = @{ id = "org.bap.examples.host-wallpaper"; name = "BAP Host Wallpaper Demo"; version = "0.1.0" }
        supportedVersions = @("0.1.0")
        capabilities = @("component-discovery", "config")
        requestedPermissions = @("component.discovery", "config.read", "config.write")
    }
} | ConvertTo-Json -Depth 8
$bytes = [Text.Encoding]::UTF8.GetBytes($hello)
$ws.SendAsync($bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
$buffer = New-Object byte[] 8192
$received = $ws.ReceiveAsync($buffer, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$response = [Text.Encoding]::UTF8.GetString($buffer, 0, $received.Count) | ConvertFrom-Json
if (-not $response.ok) { throw "Companion WebSocket hello was rejected" }
Assert-Equal $response.result.identity.id "org.bap.companion" "companion identity"
if ($response.result.permissions.Count -lt 2) { throw "Companion did not grant expected permissions" }
$ws.Dispose()

[pscustomobject]@{
    status = "passed"
    sessionId = $session.sessionId
    discoveredComponents = @($components.value).Count
    wallpaperId = $WallpaperId
    websocketHello = $true
    grantedPermissions = @($response.result.permissions)
} | ConvertTo-Json -Depth 6
