<#
.SYNOPSIS
Starts the local AgentCon buyer and three wine suppliers.

.DESCRIPTION
Builds the Rust suppliers and .NET buyer, starts them on loopback, optionally
starts the Aspire dashboard with Podman, and opens the buyer dashboard.
SLIMRPC is opt-in; the default demo uses HTTP JSON-RPC.

Same settings as start-demo.sh (parameters or environment variables):
SLIM=public|local, SLIM_IDENTITY=shared-secret|did-key|did-web, SLIM_NAMESPACE,
SELLERS=local|cloud. SELLERS=cloud (with SLIM=public and SLIM_IDENTITY=did-web)
starts only the buyer and uses the sellers running in the SLIM cluster, found
through catalog.cloud.json; their HTTPS interfaces get the slimctl login as a
bearer token. AGENTCON_BIN_DIR, when set, installs the built supplier and buyer
there and runs them from that copy (build outputs can then live elsewhere);
unset, the agents run from their build outputs.

.PARAMETER BuyerArgs
Additional arguments forwarded to the BuyerAgent after its default options.

.PARAMETER NoTraces
Skip the Aspire dashboard and OTLP trace export (same as TRACES=0).

.PARAMETER NoBrowser
Do not open browser tabs after startup.

.PARAMETER CheckOnly
Validate required local files, commands, configuration and ports without
building or starting services.

.EXAMPLE
.\start-demo.ps1

.EXAMPLE
.\start-demo.ps1 -SupplierVerifierUrl https://agentcon-issuer.azurewebsites.net

.EXAMPLE
.\start-demo.ps1 -BuyerArgs @('--Buyer:NativeStructuredSupport=true')

.EXAMPLE
.\start-demo.ps1 -Slim public -SlimIdentity did-web -Sellers cloud
#>
[CmdletBinding()]
param(
    [string]$CopilotGhUser,
    [string]$CopilotModel,
    [string]$BaroloInventory,
    [string]$SardiniaInventory,
    [string]$FranceInventory,
    [string]$SupplierVerifierUrl,
    [string]$SupplierVerifierToken,
    [string]$Slim,
    [string]$SlimNamespace,
    [string]$SlimIdentity,
    [string]$Sellers,
    [string]$BinDir,
    [string[]]$BuyerArgs = @(),
    [switch]$NoTraces,
    [switch]$NoBrowser,
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Setting([hashtable]$BoundParameters, [string]$Parameter, [string]$Environment, [string]$Default) {
    if ($BoundParameters.ContainsKey($Parameter)) { return (Get-Variable -Name $Parameter -Scope Script -ValueOnly) }
    $value = [Environment]::GetEnvironmentVariable($Environment)
    if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    return $Default
}

function Assert-Command([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found. $InstallHint"
    }
}

function Test-LoopbackPortAvailable([int]$Port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
        return $true
    } catch {
        return $false
    } finally {
        $listener.Stop()
    }
}

function Invoke-Build([string]$Description, [scriptblock]$Build) {
    Write-Host $Description
    & $Build
    if ($LASTEXITCODE -ne 0) { throw "$Description failed (exit code $LASTEXITCODE)." }
}

function Start-LoggedProcess(
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$WorkingDirectory,
    [hashtable]$Environment,
    [string]$LogPath
) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    foreach ($key in @($start.Environment.Keys)) { [void]$start.Environment.Remove($key) }
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) {
        $start.Environment[[string]$key] = [string][Environment]::GetEnvironmentVariable([string]$key)
    }
    foreach ($key in $Environment.Keys) {
        if ($null -eq $Environment[$key]) {
            [void]$start.Environment.Remove([string]$key)
        } else {
            $start.Environment[[string]$key] = [string]$Environment[$key]
        }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start '$FilePath'." }

    $stdoutPath = $LogPath
    $stderrPath = [IO.Path]::ChangeExtension($LogPath, '.stderr.log')
    $stdoutFile = [IO.File]::Open($stdoutPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $stderrFile = [IO.File]::Open($stderrPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
    return [pscustomobject]@{
        Process = $process
        Stdout = $stdoutFile
        Stderr = $stderrFile
        OutputTasks = @($stdoutTask, $stderrTask)
        LogPath = $stdoutPath
        ErrorLogPath = $stderrPath
    }
}

function Stop-LoggedProcess($Entry) {
    if (-not $Entry) { return }
    try {
        if (-not $Entry.Process.HasExited) { $Entry.Process.Kill($true) }
        $Entry.Process.WaitForExit()
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$Entry.OutputTasks, 5000) | Out-Null
    } catch {
        Write-Warning "Could not stop process $($Entry.Process.Id): $($_.Exception.Message)"
    } finally {
        $Entry.Stdout.Dispose()
        $Entry.Stderr.Dispose()
        $Entry.Process.Dispose()
    }
}

function Read-Tail([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Tail 20 }
}

$repo = $PSScriptRoot
Push-Location $repo
$boundParameters = $PSBoundParameters
$copilotUser = Get-Setting $boundParameters 'CopilotGhUser' 'COPILOT_GH_USER' 'lumuscar_cisco'
$model = Get-Setting $boundParameters 'CopilotModel' 'COPILOT_MODEL' 'gpt-6-luna'
$inventoryRoot = Join-Path $repo 'SupplierAgent\inventories'
$inventoryFiles = @(
    (Get-Setting $boundParameters 'BaroloInventory' 'BAROLO_INVENTORY' (Join-Path $inventoryRoot 'barolo.json')),
    (Get-Setting $boundParameters 'SardiniaInventory' 'SARDINIA_INVENTORY' (Join-Path $inventoryRoot 'sardinia.json')),
    (Get-Setting $boundParameters 'FranceInventory' 'FRANCE_INVENTORY' (Join-Path $inventoryRoot 'france.json'))
)
$ports = @(6000, 6100, 6200, 5100)
$tracesEnabled = -not $NoTraces -and $env:TRACES -ne '0'
$slimMode = Get-Setting $boundParameters 'Slim' 'SLIM' ''
$slimNamespaceValue = Get-Setting $boundParameters 'SlimNamespace' 'SLIM_NAMESPACE' 'agentcon'
$slimIdentityValue = Get-Setting $boundParameters 'SlimIdentity' 'SLIM_IDENTITY' 'shared-secret'
$sellersValue = Get-Setting $boundParameters 'Sellers' 'SELLERS' 'local'
$binDirectory = Get-Setting $boundParameters 'BinDir' 'AGENTCON_BIN_DIR' ''
$localSellers = $sellersValue -eq 'local'
$verifierUrl = Get-Setting $boundParameters 'SupplierVerifierUrl' 'SUPPLIER_VERIFIER_URL' ''
$verifierToken = Get-Setting $boundParameters 'SupplierVerifierToken' 'SUPPLIER_VERIFIER_TOKEN' ''
$stateRoot = Join-Path $env:LOCALAPPDATA 'AgentConDemo'
$logDirectory = Join-Path $repo 'artifacts\demo-logs'
$dashboardImage = 'mcr.microsoft.com/dotnet/aspire-dashboard:9.5'
$dashboardName = 'agentcon-dashboard'
$dashboardLogin = $null
$startedProcesses = [System.Collections.Generic.List[object]]::new()

try {
    if ($slimMode -notin @('', 'public', 'local')) { throw 'Slim must be empty, public, or local.' }
    if ($slimIdentityValue -notin @('shared-secret', 'did-key', 'did-web')) {
        throw 'SlimIdentity must be shared-secret, did-key, or did-web.'
    }
    if ($sellersValue -notin @('local', 'cloud')) { throw 'Sellers must be local or cloud.' }
    if (-not $localSellers -and ($slimMode -ne 'public' -or $slimIdentityValue -ne 'did-web')) {
        throw 'Sellers cloud needs Slim public and SlimIdentity did-web.'
    }
    # Cloud sellers run in the SLIM cluster: only the buyer starts here.
    if (-not $localSellers) { $ports = @(5100) }
    if (($verifierUrl.Length -gt 0) -xor ($verifierToken.Length -gt 0)) {
        if ($verifierUrl.Length -gt 0 -and -not $verifierToken -and [Environment]::UserInteractive) {
            $secureToken = Read-Host "Issuer admin token for $verifierUrl" -AsSecureString
            $verifierToken = [Net.NetworkCredential]::new('', $secureToken).Password
        } else {
            throw 'Set both SupplierVerifierUrl and SupplierVerifierToken, or neither.'
        }
    }

    foreach ($file in $(if ($localSellers) { $inventoryFiles } else { @() })) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Inventory file not found: $file" }
    }
    if ($slimMode -eq 'local') { $ports += 46357 }
    foreach ($port in $ports) {
        if (-not (Test-LoopbackPortAvailable $port)) {
            throw "Port $port is already in use. Stop or reconfigure that service; this script will not kill it."
        }
    }
    Assert-Command 'gh' 'Install GitHub CLI and run gh auth login.'
    Assert-Command 'dotnet' 'Install the .NET 10 SDK.'
    Assert-Command 'cargo' 'Install Rust and the required Windows MSVC build tools.'
    $libclangCandidates = @(
        if ($env:LIBCLANG_PATH) {
            $env:LIBCLANG_PATH
        } else {
            Get-Command clang.exe -CommandType Application -All -ErrorAction SilentlyContinue |
                ForEach-Object { Split-Path -Parent $_.Source }
        }
    )
    $libclangDirectory = $libclangCandidates | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_ 'libclang.dll') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $_ 'clang.dll') -PathType Leaf)
    } | Select-Object -First 1
    if (-not $libclangDirectory) {
        throw 'Rust bindgen requires libclang matching the Rust host architecture (x64 for the ARM64 demo build). Set LIBCLANG_PATH to its DLL directory; see SupplierAgent\README.md.'
    }
    $rustToolchain = $env:RUSTUP_TOOLCHAIN
    $vcvars = $null
    $vsInstallerDirectory = $null
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) {
        Assert-Command 'rustup' 'Install rustup and an x86_64-pc-windows-msvc toolchain for the x64 demo build.'
        if (-not $rustToolchain) {
            $installedToolchains = @(& rustup toolchain list)
            if ($LASTEXITCODE -ne 0) { throw 'Could not list installed Rust toolchains.' }
            $rustToolchain = $installedToolchains |
                ForEach-Object { ($_ -split '\s+')[0] } |
                Where-Object { $_ -match '-x86_64-pc-windows-msvc$' } |
                Select-Object -First 1
        }
        if (-not $rustToolchain -or $rustToolchain -notmatch '^[A-Za-z0-9_.-]+-x86_64-pc-windows-msvc$') {
            throw 'The Windows ARM64 demo uses x64 Rust/MSVC. Install an x86_64-pc-windows-msvc toolchain and select it with RUSTUP_TOOLCHAIN.'
        }
        $vswhereCommand = Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue
        $vswhere = if ($vswhereCommand) { $vswhereCommand.Source } else {
            Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        }
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            throw 'Visual Studio discovery tool vswhere.exe was not found. Install Visual Studio C++ Build Tools.'
        }
        $vsRoot = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'No Visual Studio installation with x64 C++ tools was found.' }
        $vcvars = Join-Path $vsRoot 'VC\Auxiliary\Build\vcvarsall.bat'
        if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw "Visual Studio build environment not found: $vcvars" }
        $vsInstallerDirectory = Split-Path -Parent $vswhere
    }
    if ($tracesEnabled) { Assert-Command 'podman' 'Install Podman Desktop and initialize a Podman machine.' }
    if ($slimMode) {
        Assert-Command 'slimctl' 'Install slimctl; see docs\windows-setup.md.'
        if ($slimMode -eq 'local') { Assert-Command 'python' 'Install Python 3 for SLIM identity config creation.' }
        if ($slimIdentityValue -in @('did-key', 'did-web')) {
            Assert-Command 'python' 'Install Python 3 for SLIM identity config creation.'
            Assert-Command 'openssl' 'Install OpenSSL CLI for per-agent key creation (Git for Windows includes it).'
        }
    }

    if ($CheckOnly) {
        Write-Host "Configuration is valid. HTTP ports: $($ports -join ', '); traces: $tracesEnabled; SLIM: $(if ($slimMode) { "$slimMode ($slimIdentityValue)" } else { 'disabled' }); sellers: $sellersValue."
        return
    }

    $copilotToken = (& gh auth token --user $copilotUser 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($copilotToken)) {
        throw "No gh token for '$copilotUser'. Run gh auth login for that account."
    }
    $randomBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
    $controlToken = [Convert]::ToBase64String($randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    if ($tracesEnabled) {
        $podmanInfo = & podman info 2>&1
        if ($LASTEXITCODE -ne 0) {
            $machines = (& podman machine list --format json 2>$null | Out-String) | ConvertFrom-Json
            $defaultMachine = @($machines | Where-Object Default | Select-Object -First 1)
            if ($defaultMachine.Count -eq 0) { throw 'Podman is unavailable and no default Podman machine exists. Run podman machine init, then retry.' }
            if (-not $defaultMachine[0].Running) {
                Write-Host "Starting Podman machine '$($defaultMachine[0].Name)'..."
                & podman machine start $defaultMachine[0].Name
                if ($LASTEXITCODE -ne 0) { throw 'Could not start the Podman machine.' }
            }
            & podman info *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Podman is not ready. Check podman machine list and podman info.' }
        }

        $containerJson = (& podman ps -a --filter "name=^$dashboardName$" --format json 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not list Podman containers. Check podman ps -a.' }
        $containers = @(
            if ($containerJson) { $containerJson | ConvertFrom-Json }
        )
        if ($containers.Count -gt 0) {
            $container = $containers[0]
            $imageName = (& podman inspect --format '{{.ImageName}}' $dashboardName 2>$null | Out-String).Trim()
            if ($imageName -notmatch 'aspire-dashboard') {
                throw "A container named '$dashboardName' exists but is not an Aspire dashboard; leaving it untouched."
            }
            if ($container.State -ne 'running') {
                & podman start $dashboardName
                if ($LASTEXITCODE -ne 0) { throw "Could not start existing container '$dashboardName'." }
            } else {
                Write-Host "Reusing running Aspire dashboard container '$dashboardName'."
            }
        } else {
            Write-Host "Starting Aspire dashboard with Podman ($dashboardImage)..."
            & podman run --detach --name $dashboardName `
                --publish 127.0.0.1:18888:18888 `
                --publish 127.0.0.1:4317:18889 `
                --publish 127.0.0.1:4318:18890 `
                $dashboardImage | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Podman could not start the Aspire dashboard.' }
        }

        for ($attempt = 0; $attempt -lt 30 -and -not $dashboardLogin; $attempt++) {
            $dashboardLogs = (& podman logs $dashboardName 2>&1 | Out-String)
            $match = [regex]::Match($dashboardLogs, 'http://[^\s]+login\?t=[A-Za-z0-9_-]+')
            if ($match.Success) { $dashboardLogin = $match.Value }
            else { Start-Sleep -Seconds 1 }
        }
        if (-not $dashboardLogin) {
            throw "Aspire dashboard did not become ready. Inspect with: podman logs $dashboardName"
        }
    }

    if ($slimMode) {
        $slimDirectory = Join-Path $stateRoot 'slim'
        New-Item -ItemType Directory -Force -Path $slimDirectory | Out-Null
        $slimConfigPath = Join-Path $slimDirectory 'slim-client.json'
        $slimOrgNamespace = "agntcy/$slimNamespaceValue"
        $didWebDirectory = Join-Path $stateRoot 'slim-didweb'
        if ($slimMode -eq 'public') {
            $slimTokenPath = Join-Path $HOME '.slimctl\token'
            if (-not (Test-Path -LiteralPath $slimTokenPath)) {
                throw "No slimctl token at $slimTokenPath. Run slimctl login; see docs\windows-setup.md."
            }
            try {
                $jwt = (Get-Content -LiteralPath $slimTokenPath -Raw).Trim().Split('.')[1].Replace('-', '+').Replace('_', '/')
                $jwt = $jwt.PadRight($jwt.Length + (4 - $jwt.Length % 4) % 4, '=')
                $expiry = [DateTimeOffset]::FromUnixTimeSeconds([long]([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($jwt)) | ConvertFrom-Json).exp)
                if ($expiry -lt [DateTimeOffset]::UtcNow.AddMinutes(5)) { throw 'expired' }
            } catch { throw 'SLIM token is invalid or expires in under five minutes. Run slimctl login again.' }
            $slimClient = @{
                endpoint = 'https://oidc.slim.outshift.io:443'
                tls = @{ insecure = $false; include_system_ca_certs_pool = $true }
                auth = @{ type = 'static_jwt'; file = $slimTokenPath; duration = '5m' }
            }
        } else {
            $slimNodePath = Join-Path $slimDirectory 'node.yaml'
            @'
# Local loopback-only SLIM data-plane node for the demo.
runtime: { n_cores: 0, thread_name: "slim-data-plane", drain_timeout: 10s }
services:
  slim/0:
    dataplane:
      servers:
        - endpoint: "127.0.0.1:46357"
          tls: { insecure: true }
      clients: []
'@ | Set-Content -LiteralPath $slimNodePath -Encoding utf8
            if (-not (Test-LoopbackPortAvailable 46357)) {
                throw 'SLIM local node port 46357 is occupied. This script will not reuse an unknown listener.'
            }
            New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
            $slimNode = Start-LoggedProcess 'slimctl' @('slim', 'start', '--config', $slimNodePath) `
                $repo @{} (Join-Path $logDirectory 'slim-node.log')
            $startedProcesses.Add($slimNode)
            $nodeReady = $false
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                if (-not (Test-LoopbackPortAvailable 46357)) { $nodeReady = $true; break }
                Start-Sleep -Seconds 1
            }
            if (-not $nodeReady) { throw "Local SLIM node did not listen on 46357. See $($slimNode.LogPath)." }
            $slimClient = @{ endpoint = 'http://127.0.0.1:46357'; tls = @{ insecure = $true } }
        }

        $secretPath = Join-Path $slimDirectory 'shared-secret'
        if (-not (Test-Path -LiteralPath $secretPath)) {
            $secretBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
            [Convert]::ToBase64String($secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') |
                Set-Content -LiteralPath $secretPath -NoNewline
        }
        $slimSharedSecret = (Get-Content -LiteralPath $secretPath -Raw).Trim()
        if ($slimIdentityValue -eq 'shared-secret') {
            $identity = @{ type = 'shared_secret'; id = "$slimOrgNamespace/cli"; data = $slimSharedSecret }
            @{ client = $slimClient; app = @{ name = "$slimOrgNamespace/cli"; identity_provider = $identity; identity_verifier = $identity } } |
                ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $slimConfigPath
        } elseif ($slimIdentityValue -eq 'did-key') {
            $agents = @('cli', 'barolo-supplier', 'sardinian-supplier', 'french-supplier', 'buyer')
            $identityArguments = @('-I', (Join-Path $repo 'tools\slim\slim_identities.py'), $slimDirectory,
                ($slimClient | ConvertTo-Json -Compress -Depth 10), $slimOrgNamespace) + $agents
            & python @identityArguments | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { throw 'SLIM did:key identity generation failed.' }
            Copy-Item -LiteralPath (Join-Path $slimDirectory 'cli.json') -Destination $slimConfigPath -Force
        } else {
            # Keys and configs in their own directory; the trust list is the JWKS the did:web domain publishes.
            $agents = @('cli', 'buyer', 'barolo-supplier', 'sardinian-supplier', 'french-supplier')
            $identityArguments = @('-I', (Join-Path $repo 'tools\slim\slim_identities.py'), '--did-web',
                'agents.slim.outshift.io/agentcon', $didWebDirectory, ($slimClient | ConvertTo-Json -Compress -Depth 10),
                $slimOrgNamespace) + $agents
            Write-Host 'SLIM identities (did:web):'
            & python @identityArguments | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { throw 'SLIM did:web identity generation failed.' }
            try {
                Invoke-WebRequest -Uri 'https://agents.slim.outshift.io/.well-known/jwks.json' -TimeoutSec 20 `
                    -MaximumRedirection 0 -OutFile (Join-Path $didWebDirectory 'site\.well-known\jwks.json')
            } catch { throw 'Cannot fetch the published JWKS from https://agents.slim.outshift.io.' }
            Copy-Item -LiteralPath (Join-Path $didWebDirectory 'cli.json') -Destination $slimConfigPath -Force
        }
    }

    Invoke-Build 'Building Rust suppliers...' {
        $previousLibclangPath = $env:LIBCLANG_PATH
        try {
            $env:LIBCLANG_PATH = $libclangDirectory
            $cargoArguments = @()
            if ($rustToolchain) { $cargoArguments += "+$rustToolchain" }
            if ($vcvars) {
                $buildCommand = 'set "PATH=' + $vsInstallerDirectory + ';%PATH%" && call "' +
                    $vcvars + '" x64 >nul && cargo ' + $cargoArguments[0] +
                    ' build --manifest-path SupplierAgent\Cargo.toml --locked --quiet'
                & $env:ComSpec /d /s /c $buildCommand
            } else {
                & cargo @cargoArguments build --manifest-path SupplierAgent\Cargo.toml --locked --quiet
            }
        } finally {
            $env:LIBCLANG_PATH = $previousLibclangPath
        }
    }
    $supplierExecutable = Join-Path $repo 'SupplierAgent\target\debug\agentcon-supplier.exe'
    $buyerCommand = @('run', '--no-build', '--project', 'BuyerAgent\BuyerAgent.csproj', '--')
    if ($binDirectory) {
        # Run from an installed copy, so the build outputs can live elsewhere (e.g. an external drive).
        New-Item -ItemType Directory -Force -Path $binDirectory | Out-Null
        Copy-Item -LiteralPath $supplierExecutable -Destination (Join-Path $binDirectory 'agentcon-supplier.exe') -Force
        $supplierExecutable = Join-Path $binDirectory 'agentcon-supplier.exe'
        Invoke-Build 'Publishing .NET buyer...' {
            & dotnet publish BuyerAgent\BuyerAgent.csproj -c Debug --nologo -v quiet -o (Join-Path $binDirectory 'buyer')
        }
        $buyerCommand = @((Join-Path $binDirectory 'buyer\BuyerAgent.dll'))
    } else {
        Invoke-Build 'Building .NET buyer...' {
            & dotnet build BuyerAgent\BuyerAgent.csproj --nologo -v quiet
        }
    }

    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $supplierNames = @('supplier', 'sardinia', 'france')
    $supplierApps = @('barolo-supplier', 'sardinian-supplier', 'french-supplier')
    $supplierProcesses = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $(if ($localSellers) { 3 } else { 0 }); $index++) {
        $supplierState = Join-Path $stateRoot "$($supplierNames[$index])-copilot"
        New-Item -ItemType Directory -Force -Path $supplierState | Out-Null
        $supplierEnvironment = @{
            COPILOT_GITHUB_TOKEN = $copilotToken
            SUPPLIER_PORT = @('6000', '6100', '6200')[$index]
            SUPPLIER_INVENTORY = $inventoryFiles[$index]
            SUPPLIER_MODEL = $model
            SUPPLIER_COPILOT_STATE = $supplierState
            SUPPLIER_DEMO_CONTROL_TOKEN = $controlToken
            SUPPLIER_VERIFIER_URL = if ($verifierUrl) { $verifierUrl } else { $null }
            SUPPLIER_VERIFIER_TOKEN = if ($verifierToken) { $verifierToken } else { $null }
            SUPPLIER_SLIM_CONFIG = $null
            SUPPLIER_SLIM_NAME = $null
            SLIM_SHARED_SECRET = $null
        }
        if ($tracesEnabled) {
            $supplierEnvironment.OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'
            $supplierEnvironment.OTEL_EXPORTER_OTLP_PROTOCOL = 'grpc'
        } else {
            $supplierEnvironment.OTEL_EXPORTER_OTLP_ENDPOINT = $null
            $supplierEnvironment.OTEL_EXPORTER_OTLP_PROTOCOL = $null
        }
        if ($slimMode) {
            $supplierEnvironment.SUPPLIER_SLIM_NAME = "$slimOrgNamespace/$($supplierApps[$index])"
            $supplierEnvironment.SLIM_SHARED_SECRET = $slimSharedSecret
            $supplierEnvironment.SUPPLIER_SLIM_CONFIG = switch ($slimIdentityValue) {
                'did-key' { Join-Path $slimDirectory "$($supplierApps[$index]).json" }   # its own key and the trust list
                'did-web' { Join-Path $didWebDirectory "$($supplierApps[$index]).json" }
                default { $slimConfigPath }
            }
        }
        $process = Start-LoggedProcess $supplierExecutable `
            @() $repo $supplierEnvironment (Join-Path $logDirectory "$($supplierNames[$index]).log")
        $startedProcesses.Add($process)
        $supplierProcesses.Add($process)
    }

    $buyerState = Join-Path $stateRoot 'buyer-copilot'
    New-Item -ItemType Directory -Force -Path $buyerState | Out-Null
    $buyerEnvironment = @{
        COPILOT_GITHUB_TOKEN = $copilotToken
        Buyer__SupplierControlToken = $controlToken
        Buyer__Model = $model
        Buyer__ExchangeCaptureEnabled = 'true'
        Buyer__SlimSharedSecret = if ($slimMode -and $slimIdentityValue -eq 'shared-secret') { $slimSharedSecret } else { $null }
        OTEL_EXPORTER_OTLP_ENDPOINT = if ($tracesEnabled) { 'http://localhost:4317' } else { $null }
        OTEL_EXPORTER_OTLP_PROTOCOL = if ($tracesEnabled) { 'grpc' } else { $null }
    }
    $buyerArguments = $buyerCommand + @(
        '--Buyer:ExchangeCaptureEnabled=true', "--Buyer:Model=$model",
        "--Buyer:CopilotStateDirectory=$buyerState")
    if ($slimMode) {
        # The buyer's own identity (did-key, did-web); with a shared secret every app uses the common file.
        $buyerSlimConfig = switch ($slimIdentityValue) {
            'did-key' { Join-Path $slimDirectory 'buyer.json' }
            'did-web' { Join-Path $didWebDirectory 'buyer.json' }
            default { $slimConfigPath }
        }
        $buyerArguments += "--Buyer:SlimConfigPath=$buyerSlimConfig"
        $buyerArguments += "--Buyer:SlimName=$slimOrgNamespace/buyer"
    }
    if (-not $localSellers) {
        # Cloud sellers' HTTPS interfaces sit behind the OIDC gateway: the buyer sends the slimctl login as a
        # bearer token, to that host only.
        $buyerArguments += "--Buyer:CatalogUrl=$(Join-Path $repo 'catalog.cloud.json')"
        $buyerArguments += "--Buyer:SupplierBearerTokenFile=$slimTokenPath"
        $buyerArguments += '--Buyer:SupplierBearerTokenOrigins:0=https://agents.slim.outshift.io'
    }
    $buyerArguments += $BuyerArgs
    $buyerProcess = Start-LoggedProcess 'dotnet' $buyerArguments $repo $buyerEnvironment `
        (Join-Path $logDirectory 'buyer.log')
    $startedProcesses.Add($buyerProcess)

    $healthTargets = @(
        if ($localSellers) {
            @{ Name = 'Barolo supplier'; Url = 'http://localhost:6000/health'; Log = $supplierProcesses[0] }
            @{ Name = 'Sardinian supplier'; Url = 'http://localhost:6100/health'; Log = $supplierProcesses[1] }
            @{ Name = 'French supplier'; Url = 'http://localhost:6200/health'; Log = $supplierProcesses[2] }
        }
        @{ Name = 'Buyer'; Url = 'http://localhost:5100/health'; Log = $buyerProcess }
    )
    foreach ($target in $healthTargets) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($target.Log.Process.HasExited) {
                Read-Tail $target.Log.LogPath
                Read-Tail $target.Log.ErrorLogPath
                throw "$($target.Name) exited before becoming healthy."
            }
            try {
                $null = Invoke-RestMethod -Uri $target.Url -TimeoutSec 2
                $ready = $true
                break
            } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) {
            Read-Tail $target.Log.LogPath
            Read-Tail $target.Log.ErrorLogPath
            throw "$($target.Name) did not become healthy at $($target.Url)."
        }
        Write-Host "  $($target.Name) ready: $($target.Url)"
    }

    if (-not $verifierUrl) {
        Write-Warning 'Wallet verifier not configured; purchase tasks will stop at auth-required.'
    }
    if (-not $NoBrowser) {
        Start-Process 'http://localhost:5100/demo'
        if ($tracesEnabled) { Start-Process $dashboardLogin }
    }

    Write-Host "`nModel: $model"
    Write-Host $(if ($slimMode) { "Transport: SLIMRPC ($slimMode, identity $slimIdentityValue, sellers $sellersValue), HTTP JSON-RPC as fallback." }
        else { 'Transport: HTTP JSON-RPC (SLIMRPC is opt-in).' })
    Write-Host 'Buyer dashboard: http://localhost:5100/demo'
    if ($tracesEnabled) { Write-Host "Aspire: $dashboardLogin`nOTLP: http://localhost:4317 (gRPC)" }
    else { Write-Host 'Traces: disabled' }
    Write-Host "Logs: $logDirectory"
    Write-Host "`nTry:"
    if ($slimMode) {
        Write-Host "  `$env:A2A_SLIMRPC_PLUGIN_CONFIG = '$slimConfigPath'"
        Write-Host "  a2a send -e slim://$slimOrgNamespace/buyer --transport slimrpc --timeout 150s --context-id dinner 'Search the catalog for wine suppliers and ask for a demo quote for 12 cases of 2019 Barolo delivered to San Jose. Do not purchase.'"
        Write-Host "  a2a send -e slim://$slimOrgNamespace/buyer --transport slimrpc --timeout 150s --context-id dinner 'Buy the quoted wine as a simulated demo order only.'"
    } else {
        Write-Host "  a2a send -a http://localhost:5100 --timeout 150s --context-id dinner 'Search the catalog for wine suppliers, select urn:agentcon:agent:demo-wine-supplier, and ask for a demo quote for 12 cases of 2019 Barolo delivered to San Jose. Do not purchase.'"
        Write-Host "  a2a send -a http://localhost:5100 --timeout 150s --context-id dinner 'Buy the quoted wine as a simulated demo order only.'"
    }
    Write-Host "`nPress Ctrl+C to stop the started agent processes."
    while ($true) {
        foreach ($entry in $startedProcesses) {
            if ($entry.Process.HasExited) {
                Read-Tail $entry.LogPath
                Read-Tail $entry.ErrorLogPath
                throw "A demo process exited (PID $($entry.Process.Id)); see $logDirectory."
            }
        }
        Start-Sleep -Seconds 2
    }
} finally {
    foreach ($entry in $startedProcesses) { Stop-LoggedProcess $entry }
    $copilotToken = $null
    $verifierToken = $null
    $slimSharedSecret = $null
    $controlToken = $null
    Pop-Location
}
