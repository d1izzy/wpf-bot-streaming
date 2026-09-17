param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionUrl,
    [string]$ProxyCoreExePath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$proxyRuntimeDir = Join-Path $repoRoot "proxy_runtime"
$configPath = Join-Path $proxyRuntimeDir "config.json"
New-Item -Path $proxyRuntimeDir -ItemType Directory -Force | Out-Null
$response = Invoke-WebRequest -Uri $SubscriptionUrl -UseBasicParsing
$json = $response.Content | ConvertFrom-Json
if ($null -eq $json) { throw "Subscription response is empty or invalid JSON." }
if ($json -is [System.Array]) { if ($json.Count -eq 0) { throw "Subscription list is empty." }; $selectedConfig = $json[0] } else { $selectedConfig = $json }
$selectedConfig | ConvertTo-Json -Depth 100 | Set-Content -Path $configPath -Encoding UTF8
if (![string]::IsNullOrWhiteSpace($ProxyCoreExePath)) {
    if (!(Test-Path $ProxyCoreExePath)) { throw "Proxy core executable not found: $ProxyCoreExePath" }
    Copy-Item -Path $ProxyCoreExePath -Destination (Join-Path $proxyRuntimeDir (Split-Path -Leaf $ProxyCoreExePath)) -Force
}
if (!(Test-Path (Join-Path $proxyRuntimeDir "sing-box.exe")) -and !(Test-Path (Join-Path $proxyRuntimeDir "xray.exe"))) {
    Write-Warning "No proxy core executable found in proxy_runtime. Place sing-box.exe or xray.exe here."
}
Write-Host "Config saved to: $configPath" -ForegroundColor Green
