param(
    [Parameter(Mandatory = $true)]
    [string] $SteamUsername,

    [string] $Repository = "romaine-life/spirelens",

    [string] $ForgejoUrl = "https://forgejo.romaine.life"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$credential = "protocol=https`nhost=forgejo.romaine.life`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw "Forgejo Git credentials are required." }
$passwordLine = $credential | Where-Object { $_ -like 'password=*' }
if (-not $passwordLine) { throw "Forgejo Git credentials are required." }
$headers = @{Authorization = 'token ' + $passwordLine.Substring(9)}
$publisherRoot = Join-Path $env:LOCALAPPDATA "SpireLens/steamcmd-publisher"
$steamArchive = Join-Path $publisherRoot "steamcmd.zip"
$steamExecutable = Join-Path $publisherRoot "steamcmd.exe"
$steamConfig = Join-Path $publisherRoot "config/config.vdf"
New-Item -ItemType Directory -Path $publisherRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $steamExecutable)) {
    Write-Host "Downloading SteamCMD..."
    Invoke-WebRequest `
        -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" `
        -OutFile $steamArchive
    Expand-Archive `
        -LiteralPath $steamArchive `
        -DestinationPath $publisherRoot `
        -Force
}

Write-Host ""
Write-Host "Complete the one-time password and Steam Guard login below."
Write-Host "The isolated login will be stored only in Forgejo Actions."

& $steamExecutable "+login" $SteamUsername "+quit"
if ($LASTEXITCODE -ne 0) {
    throw "SteamCMD login failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $steamConfig)) {
    throw "SteamCMD did not create its authenticated config.vdf."
}

$credentialBytes = [IO.File]::ReadAllBytes($steamConfig)
$credentialBase64 = [Convert]::ToBase64String($credentialBytes)
if ($credentialBase64.Length -ge 48KB) {
    throw "The isolated SteamCMD credential is too large for a Forgejo Actions secret."
}

Write-Host "Saving the isolated SteamCMD credential to Forgejo..."
$api = "$($ForgejoUrl.TrimEnd('/'))/api/v1/repos/$Repository/actions"
Invoke-RestMethod -Method Put -Uri "$api/secrets/STEAM_CONFIG_VDF_B64" `
    -Headers $headers -ContentType 'application/json' `
    -Body (@{data=$credentialBase64} | ConvertTo-Json) | Out-Null
try {
    Invoke-RestMethod -Method Post -Uri "$api/variables/STEAM_USERNAME" `
        -Headers $headers -ContentType 'application/json' `
        -Body (@{value=$SteamUsername} | ConvertTo-Json) | Out-Null
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 409) { throw }
    Invoke-RestMethod -Method Put -Uri "$api/variables/STEAM_USERNAME" `
        -Headers $headers -ContentType 'application/json' `
        -Body (@{value=$SteamUsername} | ConvertTo-Json) | Out-Null
}
Write-Host ""
Write-Host "Steam Workshop publishing credentials are configured."
Write-Host "Rerun this script if Steam invalidates or rotates the saved login."
