# Forgejo MCP Server — install package

Self-contained .NET 10 build of the Forgejo MCP server (stdio transport).
The .NET runtime is embedded — the target machine does **not** need a .NET
SDK or runtime installed.

## Contents

| Item | Purpose |
| --- | --- |
| `forgejo-mcp-server` | The server binary (self-contained launcher) |
| `*.dll` | .NET runtime + application assemblies |
| `appsettings.json` | Default configuration (edit or override with env vars) |
| `install.sh` | Installer: copies to `<PREFIX>/lib`, adds launcher to PATH, smoke-tests |
| `README.md` | This file |
| `LICENSE` | MIT |
| `manifest.json` | Package metadata + file checksums |

## Quick install

```sh
tar -xzf forgejo-mcp-server-<VERSION>-linux-<ARCH>.tar.gz
cd forgejo-mcp-server-<VERSION>-linux-<ARCH>
bash install.sh
```

`install.sh` will:

1. Copy the runtime into `~/.local/lib/forgejo-mcp-server` (or
   `/usr/local/lib/...` if root / writable).
2. Create a `forgejo-mcp-server` launcher on PATH in `~/.local/bin` (or
   `/usr/local/bin`).
3. Run a live MCP handshake (`initialize` + `tools/list`) to prove the
   server starts.
4. If Hermes Agent (`hermes`) is installed and `FORGEJO_URL` is set in the
   environment, register the server as `forgejo` in its MCP config.

## Configure

Configuration (highest priority first):

1. Environment variables — `FORGEJO_URL` (required),
   `FORGEJO_TOKEN` **or** `FORGEJO_USERNAME`+`FORGEJO_PASSWORD`,
   `FORGEJO_MAX_RETRIES`, `FORGEJO_RETRY_BASE_DELAY_SECONDS`
2. `appsettings.json` (or `appsettings.{Environment}.json`) next to the
   binary

Example:

```sh
export FORGEJO_URL=https://git.home.internal
export FORGEJO_TOKEN=***
```

Verify:

```sh
forgejo-mcp-server --help
forgejo-mcp-server --version
```

## Register with an MCP client

Hermes Agent:

```sh
hermes mcp add forgejo --command $HOME/.local/lib/forgejo-mcp-server/forgejo-mcp-server \
  --env FORGEJO_URL=https://git.home.internal FORGEJO_TOKEN=***
```

Other MCP hosts (JSON config):

```json
{
  "mcpServers": {
    "forgejo": {
      "command": "/path/to/forgejo-mcp-server",
      "env": {
        "FORGEJO_URL": "https://git.home.internal",
        "FORGEJO_TOKEN": "***"
      }
    }
  }
}
```

## Internal CA certificate (private instances)

If the Forgejo instance uses a private TLS CA (e.g. an internal PKI),
point the standard `SSL_CERT_FILE` / `SSL_CERT_DIR` environment variables
at the CA bundle before starting the server or its MCP client:

```sh
export SSL_CERT_FILE=/path/to/internal-ca.crt
```

The server does not disable certificate verification.

## Uninstall

```sh
rm -rf ~/.local/lib/forgejo-mcp-server ~/.local/bin/forgejo-mcp-server
hermes mcp remove forgejo   # if you registered it
```

## Reusing this package

The package can be copied to any other machine of the same architecture
(x86_64 or aarch64, Linux glibc). The binary is a self-contained
.NET app — no SDK, no runtime, no other dependencies.
