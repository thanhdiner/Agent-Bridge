# LocalMcp — Local Windows File System MCP Gateway

LocalMcp is a secure, production-shaped system that exposes the local Windows file system to AI clients (such as ChatGPT) using the Model Context Protocol (MCP). It uses a strict separation between the Control Plane (Gateway) and the Execution Plane (Windows Agent), connected via SignalR.

---

## System Architecture

```mermaid
graph TD
    Client["ChatGPT (MCP Client)"]
    Auth0["Auth0 (OAuth 2.1 / OIDC)"]

    subgraph ControlPlane["Control Plane (Gateway)"]
        McpServer["MCP HTTP Server (/)"]
        Registry["Agent Connection Registry"]
        Dispatcher["SignalR Command Dispatcher"]
        Hub["AgentHub (SignalR)"]
        JwtMiddleware["JWT Bearer Middleware"]
        ScopePolicies["Scope Policies (files:read / files:write / dev:execute)"]
    end

    subgraph ExecutionPlane["Execution Plane (Windows Agent)"]
        Conn["Gateway Connection (device token auth)"]
        Handler["Command Handler"]
        Sandbox["PathPolicy (Sandbox + WritableRoots)"]
        Executor["File System Executor (strict UTF-8)"]
        Disk["Windows File System"]
    end

    Client -->|"Bearer token (files:read, files:write, or dev:execute)"| JwtMiddleware
    JwtMiddleware -->|Validate signature/issuer/audience/lifetime| Auth0
    JwtMiddleware --> ScopePolicies
    ScopePolicies -->|fs_read/fs_batch_read/fs_read_range/fs_list/fs_tree/fs_search/fs_search_context/git_status/git_diff/git_log/git_show/project_verify/fs_stat/fs_batch_stat/fs_write/fs_patch/fs_batch_patch/fs_move/fs_copy/fs_delete/fs_rmdir| McpServer
    McpServer --> Dispatcher
    Registry -.->|Lookup Connection| Dispatcher
    Dispatcher -->|SignalR Command| Hub
    Hub -->|TCP Loopback| Conn
    Conn --> Handler
    Handler --> Sandbox
    Sandbox -->|Safe traversal / I/O| Executor
    Executor --> Disk
    Disk --> Executor
    Executor --> Handler
    Handler -->|SignalR Response| Hub
    Hub --> Dispatcher
    Dispatcher --> McpServer
    McpServer -->|JSON-RPC result| Client
```

---

## Exposed MCP Tools

### Read tools — require `files:read` scope

| Tool | Description |
|---|---|
| `fs_tree` | Returns a bounded directory tree. Used by ChatGPT to understand project layout. |
| `fs_list` | Lists immediate children of a directory, sorted directory-first then alphabetically. |
| `fs_search` | Recursively searches for files matching a query (filename or content). |
| `fs_search_context` | Searches UTF-8 files using literal or regex matching and returns bounded surrounding lines plus each file's SHA-256. Supports include/exclude globs. |
| `git_status` | Returns bounded Git working-tree status, branch/upstream metadata, ahead/behind counts, and policy-filtered changed paths. |
| `git_diff` | Returns a bounded staged or unstaged unified diff for policy-authorized paths. It can append safe synthetic patches for untracked UTF-8 files. |
| `git_log` | Returns up to 100 recent commits with author, ISO-date, literal-path, pagination, and optional short-stat filters. |
| `git_show` | Returns one resolved commit's metadata, optional policy-filtered statistics, and a bounded unified patch. |
| `fs_read` | Reads the text of a single file. Returns size, encoding, SHA-256, and content. |
| `fs_batch_read` | Reads 1–20 UTF-8 text files in one request with independent per-path errors, stable input ordering, four-way concurrency, UTF-8-safe truncation, and configurable per-file plus total response byte limits. |
| `fs_read_range` | Streams a UTF-8 text file and returns a bounded one-based line range, total line count, encoding, SHA-256, and truncation status without loading the whole file into memory. Defaults to 200 lines and allows at most 1000 lines per call. |
| `fs_stat` | Returns metadata (existence, size, SHA-256, encoding, read-only flag, last-write-time, reparse point status) of a path. Returns `Exists = false` for non-existent paths. For files larger than `MaxReadBytes`, skips content hashing/encoding detection, returning `ContentMetadataSkipped = true`. |
| `fs_batch_stat` | Returns ordered status results for 1–100 paths in one call. Each path is evaluated independently, failures do not abort the batch, and internal concurrency is capped at eight operations. |

### Execution tools — require `dev:execute` scope

| Tool | Description |
|---|---|
| <code>project_verify</code> | Detects .NET, Node.js, Rust, PHP/Laravel, Python, or Go projects and runs fixed `build`, `test`, `lint`, or `typecheck` steps with bounded output and timeout controls. Python prefers `.venv\Scripts` tools and uses fixed fallbacks; Go disables automatic toolchain and module downloads. |

> [!CAUTION]
> Project verification executes repository-defined code and may generate build artifacts. It is not an operating-system sandbox. Grant `dev:execute` only to trusted clients and run it only against trusted projects. Execution is denied entirely when `Security:AuthenticationEnabled=false`.

### Write tools — require `files:write` scope

| Tool | Description |
|---|---|
| `fs_write` | Creates or overwrites a single file using optimistic concurrency (SHA-256 ETag). |
| `fs_patch` | Applies a list of exact text substitutions atomically to an existing file. |
| `fs_batch_patch` | Applies text edits to 1–20 UTF-8 files with four-way concurrency, stable ordering, atomic writes per file, and per-item results. |
| `fs_mkdir` | Creates directory or directories. Gated by `WritableRoots` enforcement. For recursive creation (`recursive: true`), resolves the closest existing ancestor, validates it, checks every proposed subdirectory name against denied patterns, and creates them segment by segment with post-creation safety verification and automatic rollback on failure. |
| `fs_move` | Moves or renames a file or directory within writable roots. File moves support cross-volume copy-verify-delete fallback with SHA-256 verification, durable temporary writes, rollback on pre-delete failure, and optional source hash guards. Directory moves remain same-volume only. |
| `fs_copy` | Copies a file or bounded directory tree into a writable root. Directory sources require `recursive: true`, reject merge/overwrite, enforce entry and byte limits, reject reparse points at every level, and publish through a temporary sibling directory followed by an atomic rename. |
| `fs_delete` | Deletes one file from a writable root after confirmation. Directories are not supported; optional SHA-256 concurrency checks and `missingOk` are supported. |
| `fs_rmdir` | Removes one empty directory from a writable root after confirmation. Recursive deletion is not supported, configured roots are protected, and `missingOk` is supported. |
| `git_restore_file` | Restores a regular tracked file from HEAD into the working tree on a target Windows agent device. Does not modify the Git index/staging. Requires Git on the agent and files:write scope. |
| `git_refresh_index` | Refreshes the Git index for a single regular tracked file on a target Windows agent device, updating out-of-sync stat cache or line ending attributes if semantic content matches the index. Requires Git on the agent and files:write scope. |

> [!IMPORTANT]
> **Write tools (including `fs_mkdir`) are disabled by default.** `WritableRoots` in `appsettings.json` is an empty list. You must explicitly add directories before write tools can succeed.

> [!NOTE]
> All tools return structured JSON errors. Internal paths, stack traces, and `.tmp_` filenames are never leaked to MCP clients.

---

## Security Model

### OAuth 2.1 (Gateway)

All MCP tool calls require a valid Bearer token issued by Auth0.

Token validation enforces:
- **Signature** — RS256 signed by Auth0's JWKS
- **Issuer** — must match `Security:OAuth:Authority`
- **Audience** — must match `Security:OAuth:Audience`
- **Lifetime** — `nbf`/`exp` checked with zero clock skew
- **Scope** — `files:read` for read tools; `files:write` for write tools; `dev:execute` for project checks

The OAuth 2.1 protected-resource metadata is published at:
```
GET /.well-known/oauth-protected-resource
GET /.well-known/oauth-protected-resource/mcp
```

### PathPolicy sandbox (Windows Agent)

Every filesystem operation passes through `PathPolicy` before executing:

1. **Empty rejection** — null/whitespace paths rejected immediately.
2. **Canonical normalisation** — resolves absolute physical path, collapses `..` segments.
3. **Symlink/junction resolution** — recursively resolves links to check the final destination.
4. **AllowedRoots check** — resolved path must be inside a configured allowed root.
5. **Prefix collision prevention** — `F:\Project` does not grant access to `F:\Project-evil\`.
6. **DeniedSegments** — blocks `bin`, `obj`, `.git`, `.ssh`, `AppData`, etc.
7. **DeniedFileNames** — blocks exact names (`.env`, `id_rsa`) and wildcard patterns (`.env.*`).
8. **DeniedWriteFileNames** — additional write-specific name denials (`.gitconfig`, etc.).
9. **DeniedWriteExtensions** — blocks certificate/key file extensions (`.pem`, `.key`, `.pfx`, `.p12`, etc.).
10. **ReadOnly file check** — write operations reject files with the `ReadOnly` attribute.
11. **WritableRoots check** — write operations require the resolved path to be inside a writable root.
12. **Size validation** — `fs_read` rejects files > `MaxReadBytes` (default 2 MB); `fs_read_range` may scan larger files but bounds the returned range to `MaxReadBytes`; writes reject content > `MaxWriteBytes` (default 512 KB); directory copy additionally enforces caller-supplied `maxEntries` and `maxTotalBytes` limits with hard caps of 5000 entries and 1 GiB.
13. **Git inspection hardening** — Git tools are read-only, disable external diff drivers, text conversion, pagers, prompts, fsmonitor, and submodule recursion. They reject executable clean/process filters configured by the repository itself, while ignoring unrelated global filters such as Git LFS, bound process output/time, and omit paths denied by `PathPolicy`.
14. **Project execution hardening** — Project verification accepts no arbitrary command or argument string. It selects fixed commands from project adapters, resolves trusted `.venv\Scripts` executables or external toolchains from `PATH`, requires `dev:execute`, bounds output and runtime, disables interactive and color features, prevents Go toolchain/module auto-downloads, and kills the complete process tree on timeout or cancellation.

### Device token (Agent → Gateway SignalR)

The Windows Agent authenticates to the Gateway's AgentHub using a separate device bearer token configured under `AgentSecurity`.

---

## Configuration

### Windows Agent — `src/LocalMcp.Agent.Windows/appsettings.json`

```json
{
  "Agent": {
    "DeviceId": "development-machine",
    "GatewayUrl": "http://localhost:5227"
  },
  "FileAccess": {
    "AllowedRoots": [ "F:\\Your Project Root" ],
    "WritableRoots": [],
    "DeniedSegments": [ "bin", "obj", ".git", ".ssh", "node_modules" ],
    "DeniedFileNames": [ ".env", ".env.*", "id_rsa", "id_ed25519", "credentials.json" ],
    "DeniedWriteFileNames": [ ".env", ".env.*", "id_rsa", "id_ed25519", ".gitconfig" ],
    "DeniedWriteExtensions": [ ".pem", ".key", ".pfx", ".p12", ".cer", ".der" ],
    "MaxReadBytes": 2097152,
    "MaxWriteBytes": 524288
  }
}
```

> [!IMPORTANT]
> To enable write tools (`fs_write`, `fs_patch`, `fs_mkdir`, `fs_move`, `fs_copy`, `fs_delete`, and `fs_rmdir`), add the target directory to `WritableRoots`.
> Start with a dedicated scratch directory (`F:\scratch`) and only expand after testing.

### Gateway — `src/LocalMcp.Gateway/appsettings.json`

```json
{
  "Security": {
    "AuthenticationEnabled": true,
    "PublicExposure": true,
    "PublicBaseUrl": "https://mcp.yourdomain.com",
    "OAuth": {
      "Authority":       "https://your-tenant.auth0.com/",
      "Audience":        "https://mcp.yourdomain.com",
      "RequiredScopes":  [ "files:read" ]
    }
  },
  "AgentSecurity": {
    "AuthenticationEnabled": true,
    "DeviceTokens": [ "your-device-secret-token" ]
  }
}
```

Use `appsettings.Local.json` (git-ignored) for secrets. Never commit tokens to source control.

To enable project verification through Auth0, add the `dev:execute` permission to the API and request that scope from the connector. Do not grant it to ordinary read-only clients.

---

## How to Run

### 1. Start the Gateway

```powershell
dotnet run --project src/LocalMcp.Gateway -c Release
```

Or: `run-gateway.bat`

### 2. Start the Agent

```powershell
dotnet run --project src/LocalMcp.Agent.Windows -c Release
```

Or: `run-agent.bat`

### 3. Verify the connection

The Agent logs `[AgentHub] Connected as <deviceId>` when the SignalR handshake succeeds.

---

## Testing with ChatGPT

### Initial auth flow

1. Open your ChatGPT developer connector or MCP Inspector.
2. Set the MCP URL to `https://mcp.yourdomain.com/`.
3. Click **Connect** — ChatGPT fetches `/.well-known/oauth-protected-resource` and redirects to Auth0.
4. Authenticate with your Auth0 credentials.
5. ChatGPT stores the access token and begins making tool calls.

### Refreshing after schema changes

When you add or rename tools, ChatGPT must re-fetch the tool list:

1. Go to your ChatGPT settings → Connected Apps / Developer MCP Servers.
2. Find the server (`https://mcp.yourdomain.com/`) and click **Refresh** or **Re-fetch schemas**.
3. ChatGPT re-calls `tools/list` and updates its instructions.

### Reconnecting after token expiry

Auth0 access tokens expire (default: 24 h for API tokens). To re-authenticate:

1. In ChatGPT, disconnect and reconnect the MCP server.
2. Complete the Auth0 login flow again.
3. A new access token is issued and stored automatically.

---

## Recommended Write-Tool Testing Workflow

> [!WARNING]
> Do **not** add a production project directory to `WritableRoots` for initial testing.

1. Create an isolated scratch directory: `mkdir F:\mcp-scratch`
2. Add it to `WritableRoots` in `appsettings.json`.
3. Obtain a token with `files:write` scope via Auth0 device flow or test client.
4. Use MCP Inspector to call `fs_write` targeting `F:\mcp-scratch\test.txt`.
5. Verify file is created. Verify SHA-256 conflict protection by re-calling with stale hash.
6. Only after successful isolated testing, consider adding real project directories.

---

## Important Security Warnings

> [!CAUTION]
> **Public Tunnel Warning**
> The Cloudflare tunnel (`https://mcp.yourdomain.com`) makes the MCP server reachable from the internet.
> - `AuthenticationEnabled` **must be `true`** for any public-facing deployment.
> - The Gateway will **fail startup** if public exposure is enabled without authentication in non-Development environments.
> - Write tools add further risk — only enable `WritableRoots` for directories you fully control and that cannot affect system integrity.

> [!WARNING]
> **No `.env` or Key File Writes**
> `DeniedWriteFileNames` and `DeniedWriteExtensions` block writes to `.env`, `.env.*`, `id_rsa`, `id_ed25519`, `.pem`, `.key`, `.pfx`, `.p12` and other credential files. These cannot be overridden by the `files:write` scope.

---

## Project Structure

```text
LocalMcp/
├─ LocalMcp.sln
├─ Directory.Build.props
├─ src/
│  ├─ LocalMcp.Gateway/
│  │  ├─ Program.cs                      # Startup & public-exposure guardrail
│  │  ├─ DependencyInjection.cs
│  │  ├─ Hubs/AgentHub.cs               # SignalR hub
│  │  ├─ Mcp/FileSystemTools.cs         # Core MCP tools (scope-gated)
│  │  ├─ Mcp/BatchReadTools.cs          # Bounded multi-file read MCP tool
│  │  ├─ Security/
│  │  │  ├─ SecurityOptions.cs
│  │  │  └─ McpPolicies.cs              # files:read / files:write authorization policies
│  │  ├─ Connections/
│  │  │  ├─ IAgentConnectionRegistry.cs
│  │  │  └─ InMemoryAgentConnectionRegistry.cs
│  │  └─ Commands/
│  │     ├─ ICommandDispatcher.cs
│  │     └─ SignalRCommandDispatcher.cs
│  │
│  ├─ LocalMcp.Agent.Windows/
│  │  ├─ Program.cs
│  │  ├─ Worker.cs
│  │  ├─ Connection/
│  │  │  ├─ AgentOptions.cs
│  │  │  └─ GatewayConnection.cs        # SignalR client, command dispatch
│  │  ├─ Commands/CommandHandler.cs
│  │  ├─ FileSystem/
│  │  │  ├─ IFileSystemExecutor.cs
│  │  │  ├─ ITransferExecutor.cs        # Bounded file/directory copy orchestration
│  │  │  ├─ FileSystemExecutor.cs       # Atomic write, patch, read, list, search
│  │  │  ├─ FileSystemExecutor.Git.cs   # Bounded, policy-filtered Git status and diff
│  │  │  ├─ FileSystemExecutor.GitHistory.cs # Bounded Git log/show with revision and path hardening
│  │  │  └─ FileSystemExecutor.ProjectCheck.cs # Fixed project verification adapters
│  │  └─ Security/
│  │     ├─ FileAccessOptions.cs
│  │     ├─ IPathPolicy.cs
│  │     └─ PathPolicy.cs               # Sandbox + WritableRoots enforcement
│  │
│  ├─ LocalMcp.Contracts/
│  │  ├─ Commands/
│  │  │  ├─ ReadFileCommand.cs
│  │  │  ├─ ReadRangeCommand.cs
│  │  │  ├─ ListDirectoryCommand.cs
│  │  │  ├─ TreeCommand.cs
│  │  │  ├─ SearchFilesCommand.cs
│  │  │  ├─ SearchContextCommand.cs
│  │  │  ├─ GitStatusCommand.cs
│  │  │  ├─ GitDiffCommand.cs
│  │  │  ├─ GitLogCommand.cs
│  │  │  ├─ GitShowCommand.cs
│  │  │  ├─ ProjectCheckCommand.cs
│  │  │  ├─ AgentCommandTimeouts.cs
│  │  │  ├─ WriteFileCommand.cs
│  │  │  ├─ PatchFileCommand.cs
│  │  │  ├─ CreateDirectoryCommand.cs
│  │  │  ├─ StatCommand.cs
│  │  │  ├─ BatchStatCommand.cs
│  │  │  ├─ BatchReadCommand.cs
│  │  │  ├─ MoveCommand.cs
│  │  │  ├─ CopyCommand.cs
│  │  │  ├─ DeleteCommand.cs
│  │  │  └─ RemoveDirectoryCommand.cs
│  │  └─ Results/
│  │     ├─ CommandError.cs
│  │     ├─ CommandResult.cs
│  │     ├─ ReadFileResult.cs
│  │     ├─ ReadRangeResult.cs
│  │     ├─ ListDirectoryResult.cs
│  │     ├─ TreeResult.cs
│  │     ├─ SearchFilesResult.cs
│  │     ├─ SearchContextResult.cs
│  │     ├─ GitStatusResult.cs
│  │     ├─ GitDiffResult.cs
│  │     ├─ GitLogResult.cs
│  │     ├─ GitShowResult.cs
│  │     ├─ ProjectVerifyResult.cs
│  │     ├─ WriteFileResult.cs
│  │     ├─ PatchFileResult.cs
│  │     ├─ CreateDirectoryResult.cs
│  │     ├─ StatResult.cs
│  │     ├─ BatchStatResult.cs
│  │     ├─ BatchReadResult.cs
│  │     ├─ MoveResult.cs
│  │     ├─ CopyResult.cs
│  │     ├─ DeleteResult.cs
│  │     └─ RemoveDirectoryResult.cs
│  │
│  └─ LocalMcp.BuildingBlocks/
│     ├─ Errors/ErrorCodes.cs           # Standard error code constants
│     └─ Serialization/JsonOptions.cs
│
└─ tests/
   ├─ LocalMcp.UnitTests/               # PathPolicy, executor, auth, schema tests
   ├─ LocalMcp.IntegrationTests/        # End-to-end SignalR integration tests
   └─ LocalMcp.ArchitectureTests/       # Circular dependency tests
```

---

## Running Tests

```powershell
dotnet test -c Release
```

All tests use dynamic, isolated temporary directories and clean up after themselves.

### Test categories

| Suite | What it covers |
|---|---|
| `PathPolicyTests` | AllowedRoots, WritableRoots, DeniedSegments, wildcard denials |
| `WriteToolsTests` | Executor write/patch safety, BOM absence, UTF-8, conflict, temp cleanup |
| `DirectoryCreationTests` | Hardened recursive directory segment creation, rollbacks, and junction/symlink escape checks |
| `StatTests` | Bounded file metadata status, encoding detection, oversized size skips, and unreadable files handling |
| `BatchStatTests` | Ordered partial-success batches, 1–100 path validation, cancellation, denied paths, and an eight-operation concurrency cap |
| `BatchReadTests` | Ordered multi-file reads, independent item failures, binary rejection, UTF-8-safe per-file and total truncation, validation, cancellation, and a four-operation concurrency cap |
| `DeleteTests` | File-only deletion policy, writable-root enforcement, hash conflicts, read-only files, denied paths, and reparse-point rejection |
| `RemoveDirectoryTests` | Empty-directory-only removal, root protection, missing paths, non-empty races, denied paths, and reparse-point rejection |
| `ReadRangeTests` | Bounded line-range streaming, large-file reads, UTF-8/BOM validation, binary rejection, response limits, and denied paths |
| `MoveCopyTests` | File move/copy concurrency plus bounded recursive directory copy, entry/byte limits, denied descendants, destination containment, and temporary-tree cleanup |
| `CrossVolumeMoveTests` | Same-volume fast path, cross-volume copy-verify-delete fallback, overwrite rollback, source mutation detection, cancellation, and temporary-file cleanup |
| `GitToolsTests` | Git filter-scope regression coverage, porcelain status parsing, history metadata/short-stat parsing, numstat aggregation, and synthetic untracked-file patch formatting |
| `ProjectCheckTests` | Project detection, package-manager selection, fixed command planning, and extended transport timeout coverage |
| `McpToolMetadataTests` | Tool annotations (ReadOnly/Destructive/Idempotent), exact parameter schemas, forbidden internal types |
| `McpAuthorizationTests` | Real HTTP JSON-RPC with JWT — anonymous→401, scope enforcement per tool |
| `GatewayAuthTests` | Metadata endpoint, token validation, public exposure guardrail |
| `CommandDeserializerTests` | Strict command deserialization for all supported commands |
| `BenchmarkTests` | PathPolicy throughput under sustained load |
| `EndToEndTests` | Full SignalR loop: Gateway → Agent → FileSystem → Gateway |
| `ArchitectureTests` | No circular project references |
