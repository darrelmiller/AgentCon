param(
    [string] $Subscription = '327fe100-c522-4c96-875d-c465cac0915b',
    [string] $ResourceGroup = 'agentcon-issuer-rg',
    [string] $AppName = 'agentcon-issuer'
)

$ErrorActionPreference = 'Stop'
$issuerPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'IssuerAgent'
if (!(Test-Path -LiteralPath (Join-Path $issuerPath 'package-lock.json'))) {
    throw 'IssuerAgent package-lock.json is required before deployment.'
}

Push-Location $issuerPath
try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Issuer dependency installation failed.' }
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'Issuer tests failed; refusing to deploy.' }
} finally {
    Pop-Location
}

$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "agentcon-issuer-$([Guid]::NewGuid().ToString('N')).zip"
try {
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $issuerPath -File -Recurse |
            Where-Object {
                $relative = [System.IO.Path]::GetRelativePath($issuerPath, $_.FullName)
                $relative -notmatch '(^|[\\/])(node_modules|\.git|state|secrets)([\\/]|$)' -and
                $_.Name -notmatch '^\.env($|\.)|private.*\.json$|admin-token|appsettings\.Local'
            } |
            ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($issuerPath, $_.FullName).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $_.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
    } finally {
        $archive.Dispose()
    }
    az webapp deploy --subscription $Subscription --resource-group $ResourceGroup --name $AppName `
        --src-path $zipPath --type zip --clean true --restart true --output none
    if ($LASTEXITCODE -ne 0) { throw 'Azure issuer deployment failed.' }
} finally {
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
}

$hostname = az webapp show --subscription $Subscription --resource-group $ResourceGroup --name $AppName `
    --query defaultHostName --output tsv
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve deployed issuer hostname.' }
$health = Invoke-RestMethod -Uri "https://$hostname/health" -TimeoutSec 30
$health | ConvertTo-Json -Compress
