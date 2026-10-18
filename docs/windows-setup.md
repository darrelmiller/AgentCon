# Windows setup

How to talk to the demo agents over SLIM from a Windows machine: install the
A2A CLI, its SLIMRPC transport plugin and `slimctl`, log in to the SLIM node,
and send a request. Running the whole demo on Windows is covered at the end.

All commands are PowerShell. None of them need administrator rights.

| Tool | Version | Windows builds |
|---|---|---|
| Go A2A CLI (`a2a`) | 0.3.1 | x64, ARM64 |
| SLIMRPC transport plugin (`a2a-transport-slimrpc`) | 0.1.6 | x64 only |
| `slimctl` | 3.4.3 | x64, ARM64 |

On Windows 11 on ARM the x64 plugin runs under emulation. That has not been
tested yet.

## 1. A2A CLI

```powershell
winget install a2aproject.a2acli
```

Without WinGet, download `a2a_<version>_windows_amd64.zip` (or `_arm64`) from
the [a2a-cli releases](https://github.com/a2aproject/a2a-cli/releases), check it
against `checksums.txt`, and put `a2a.exe` in a folder on your `PATH`.

Open a new terminal and check:

```powershell
a2a --version
```

## 2. SLIMRPC transport plugin

The A2A CLI talks SLIM through a separate plugin executable, which it finds on
`PATH`. The a2a-rs install script downloads the latest release, checks its
SHA-256, puts `a2a-transport-slimrpc.exe` in
`%LOCALAPPDATA%\Programs\a2a-transport-slimrpc` and adds that folder to your user
`PATH`:

```powershell
irm https://raw.githubusercontent.com/a2aproject/a2a-rs/main/scripts/install-slimrpc-plugin.ps1 | iex
```

Read the [script](https://github.com/a2aproject/a2a-rs/blob/main/scripts/install-slimrpc-plugin.ps1)
first if you would rather not pipe it into a shell. Set
`$env:A2A_SLIMRPC_PLUGIN_VERSION = '0.1.6'` before running it to pin a version.

To install it by hand instead, download
`a2a-transport-slimrpc-v0.1.6-x86_64-pc-windows-msvc.zip` and its `.sha256` file
from the [release page](https://github.com/a2aproject/a2a-rs/releases/tag/a2acli-transport-slimrpc-v0.1.6).
Compare `(Get-FileHash <zip>).Hash` with the `.sha256` file, unzip, and copy
`a2a-transport-slimrpc.exe` to a folder on your `PATH`.

Open a new terminal and check that the CLI finds it:

```powershell
a2a-transport-slimrpc info     # prints JSON with "name":"slimrpc"
a2a transport list             # should list slimrpc
```

## 3. slimctl

`slimctl` logs you in to the SLIM node. It is not on WinGet, so install it from
the [slim release](https://github.com/agntcy/slim/releases/tag/slimctl-v3.4.3):

```powershell
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
$dir  = "$env:LOCALAPPDATA\Programs\slimctl"
$zip  = "$env:TEMP\slimctl-windows-$arch.zip"
Invoke-WebRequest "https://github.com/agntcy/slim/releases/download/slimctl-v3.4.3/slimctl-windows-$arch.zip" -OutFile $zip
New-Item -ItemType Directory -Force $dir | Out-Null
Expand-Archive $zip -DestinationPath $dir -Force
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$dir*") { [Environment]::SetEnvironmentVariable('Path', "$userPath;$dir", 'User') }
```

Open a new terminal and check:

```powershell
slimctl --help
```

## 4. Verifying downloads (optional)

The slimctl and plugin archives carry signed build provenance. With the
[GitHub CLI](https://cli.github.com/) installed, check an archive before you
unzip it:

```powershell
gh attestation verify $zip -R agntcy/slim                          # slimctl
gh attestation verify <plugin zip> -R a2aproject/a2a-rs            # plugin
```

## 5. Log in to the SLIM node

The demo uses the public node `oidc.slim.outshift.io:443`. Log in with an
allowlisted GitHub account:

```powershell
slimctl login --client-id slimctl --discovery-uri https://dev.idp.ads.outshift.io/.well-known/openid-configuration
```

A browser window opens. When it finishes, the token is in
`%USERPROFILE%\.slimctl\token` (or under `%HOME%` if that is set, as Git for
Windows does). The token lasts about an hour; run `slimctl login` again when
calls stop authenticating.

## 6. Configure the plugin

The plugin needs a SLIM identity that the demo agents trust. With
`SLIM_IDENTITY=did-key`, `start-demo.sh` creates one for the CLI on the machine
that runs the demo, in its SLIM state folder
(`~/Library/Application Support/AgentConDemo/slim` on macOS):

- `cli.json`: the plugin configuration;
- `keys/cli.pem`: the CLI's private key (keep it private);
- `trust.jwks.json`: the public keys of all demo agents.

Copy these three files to the Windows machine, for example to
`%USERPROFILE%\agentcon`. Then edit the three `file` paths in `cli.json` to point
at their new locations. Use forward slashes, so the paths need no JSON escaping:

| Field in `cli.json` | New value |
|---|---|
| `client.auth.file` | `C:/Users/<you>/.slimctl/token` |
| `app.identity_provider.key.key.file` | `C:/Users/<you>/agentcon/cli.pem` |
| `app.identity_verifier.key.key.file` | `C:/Users/<you>/agentcon/trust.jwks.json` |

Point the plugin at the file. This sets it for the current terminal and,
through the user environment, for new ones:

```powershell
$env:A2A_SLIMRPC_PLUGIN_CONFIG = "$env:USERPROFILE\agentcon\cli.json"
[Environment]::SetEnvironmentVariable('A2A_SLIMRPC_PLUGIN_CONFIG', $env:A2A_SLIMRPC_PLUGIN_CONFIG, 'User')
```

`A2A_SLIMRPC_PLUGIN_CONFIG` must be a file path, not inline configuration.

## 7. Send a request

With the demo running, send a request to the buyer over SLIM:

```powershell
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s 'Quote one case of 2019 Barolo delivered to San Jose, US. Do not purchase.'
```

The buyer asks every supplier in one SLIM multicast and returns their answers.
Name a supplier to talk to just that one:

```powershell
a2a send -e slim://agntcy/agentcon/buyer --transport slimrpc --timeout 150s 'Search the catalog, select urn:agentcon:agent:french-wine-supplier, and quote one case of 2015 Château Margaux delivered to San Jose, US. Do not purchase.'
```

You can also call a supplier directly, for example
`slim://agntcy/agentcon/barolo-supplier`.

## Troubleshooting

- **`a2a transport list` doesn't show `slimrpc`:** the plugin folder isn't on
  `PATH` yet. Open a new terminal.
- **`SLIM gateway connect failed`:** the slimctl token has expired. Run
  `slimctl login` again.
- **`token invalid` or no answer from the agents:** the CLI's key isn't in the
  agents' trust list. Use the `cli.json`, `cli.pem` and `trust.jwks.json` from
  the same demo run.

## Running the whole demo on Windows

For the initial HTTP JSON-RPC demo, use the native PowerShell launcher from the
repository root:

```powershell
.\start-demo.ps1
```

Install .NET 10, Rust with the Visual Studio C++ build tools, GitHub CLI
authenticated to an account with GitHub Copilot, and Podman with a Podman
machine. The launcher builds and runs the buyer and suppliers and starts the
Aspire dashboard with Podman. Use `.\start-demo.ps1 -NoTraces` to skip Podman
and Aspire.

The launcher takes the same settings as `start-demo.sh`, as parameters or
environment variables. For example, to use the sellers running in the cloud,
over SLIM and HTTPS, after `slimctl login`:

```powershell
.\start-demo.ps1 -Slim public -SlimIdentity did-web -Sellers cloud
```

`-SlimIdentity did-key` and `did-web` need Python 3 and the OpenSSL CLI on
`PATH` (Git for Windows includes OpenSSL). `-BinDir <folder>` (or
`AGENTCON_BIN_DIR`) installs the built agents in that folder and runs them from
there; without it, they run from their build outputs.

To run `start-demo.sh` itself, use WSL 2 and follow the Linux setup steps,
including Docker. The optional Windows SLIMRPC setup above is separate from the
default HTTP demo.
