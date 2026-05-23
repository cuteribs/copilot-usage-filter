# CopilotUsageFilter — Design Document

## Overview

CopilotUsageFilter is a minimal OpenTelemetry Collector that receives OTLP/HTTP traces exported by the GitHub Copilot CLI, extracts token-usage counts from each `chat` span, prints a structured log line to STDIO, and patches the matching `assistant.message` event in the Copilot session-state file.

The solution targets multiple platforms:
- **Windows** (`Windows/`): system-tray application with WinForms UI
- **Linux / macOS** (`Console/`): pure console application, no GUI

---

## Project Structure

```
CopilotUsageFilter.slnx
│
├── Core/                               # Shared library (net10.0, cross-platform)
│   ├── CopilotUsageFilter.Core.csproj
│   ├── OtlpHttpReceiver.cs
│   ├── OtlpTraceParser.cs
│   ├── SpanProcessor.cs
│   ├── SpanPayload.cs
│   └── SessionStatePatcher.cs
│
├── Console/                            # Cross-platform console app (net10.0)
│   ├── CopilotUsageFilter.Console.csproj
│   └── Program.cs
│
└── Windows/                            # Windows tray app (net10.0-windows)
    ├── CopilotUsageFilter.Windows.csproj
    ├── Program.cs
    ├── MainForm.cs
    ├── NativeMethods.cs
    ├── StartupManager.cs
    └── assets/
```

---

## Architecture

### Console (Linux / macOS / Windows)

```
Console/Program.cs
  ├─ Console.CancelKeyPress           (Ctrl+C → CancellationTokenSource)
  ├─ AppDomain.ProcessExit            (SIGTERM)
  ├─ OtlpHttpReceiver.Start()
  └─ await Task.Delay(Infinite, token)
```

### Windows (tray app)

```
Windows/Program.cs
  ├─ AttachConsole / AllocConsole     (kernel32 — bind to parent shell)
  ├─ Console.SetOut / SetError        (UTF-8 re-bind)
  └─ Application.Run(MainForm)

MainForm (hidden WinForms window)
  ├─ OtlpHttpReceiver                 (HttpListener on :4318)
  ├─ SpanProcessor                    (route → parse → log → patch)
  └─ NotifyIcon                       (system-tray icon + context menu)
        ├─ Show/Hide console toggle
        ├─ Run at startup toggle       (StartupManager → HKCU\Run)
        └─ Exit
```

### Shared data flow

```
Copilot CLI
  │  POST http://localhost:4318/v1/traces
  ▼
OtlpHttpReceiver.HandleRequestAsync
  │  (path, JSON body)
  ▼
SpanProcessor.Process
  ├─ Ignore /v1/metrics, /v1/logs     (silent return)
  ├─ OtlpTraceParser.ExtractChatSpans ─► SpanAttributes[]
  │    └─ (fallback) SpanPayload.TryParse
  │
  ├─ PrintTokens  ──────────────────────────► STDIO colored log line
  └─ PatchSession
       └─ SessionStatePatcher.PatchAssistantMessage
            └─ events.jsonl (atomic replace)
```

---

## Component Descriptions

### `Core/OtlpHttpReceiver.cs`
Wraps `HttpListener` on `http://localhost:{port}/`. Key decisions:

- **`http://localhost:`** not `http://+:` — wildcard prefixes require an elevated URL ACL reservation (`netsh http add urlacl`). Binding to `localhost` only needs no special permissions.
- Accepts POST and PUT.
- Returns `{}` with HTTP 200 on every request (standard OTLP success response).
- Fires `Func<string path, string body, Task>` callback asynchronously per request.

### `Core/OtlpTraceParser.cs`
Parses the real OTLP Protobuf-JSON format:

```json
{
  "resourceSpans": [{
    "scopeSpans": [{
      "spans": [{
        "attributes": [
          { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
          { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "1234" } }
        ]
      }]
    }]
  }]
}
```

Notable handling:
- Also accepts the older field name `instrumentationLibrarySpans`.
- `intValue` may be a quoted string **or** a bare JSON number (OTLP spec quirk). Both are handled.
- Only spans where `gen_ai.operation.name = "chat"` (case-insensitive) are returned; all others are discarded silently.
- Skips payloads that contain `resourceMetrics` or `resourceLogs` keys.

### `Core/SpanPayload.cs` / `SpanAttributes`
Fallback deserialisation model for the simplified format:

```json
{ "type": "span", "attributes": { "gen_ai.operation.name": "chat", ... } }
```

`SpanAttributes` is also the shared carrier between both parsers and the rest of the pipeline.

### `Core/SpanProcessor.cs`
Routes and dispatches incoming requests:

1. Silent return for `/v1/metrics` and `/v1/logs`.
2. Tries `OtlpTraceParser` first (real OTLP format).
3. Falls back to `SpanPayload.TryParse` (simplified format).
4. For each chat span: calls `PrintTokens` then `PatchSession`.

**`PrintTokens`** writes a colored, tab-separated line to STDIO:

```
<timestamp>  session=<8chars>  interaction=<8chars>  turn=N  input=N  output=N  cache_write=N  cache_read=N
```

Color scheme (via `Console.ForegroundColor` — no-op when output is redirected):

| Field | Color |
|---|---|
| Timestamp | DarkGray |
| `session=` value | Yellow |
| `interaction=` value | Cyan |
| `turn=` value | Magenta |
| `input=` value | Green |
| `output=` value | Blue |
| `cache_write=` value | DarkYellow |
| `cache_read=` value | DarkCyan |

GUIDs are truncated to the first 8 characters for readability (full IDs are preserved in `events.jsonl`).

### `Core/SessionStatePatcher.cs`
Locates the session folder and patches `events.jsonl`.

**Finding the session folder:**

1. Direct: `%USERPROFILE%\.copilot\session-state\{conversationId}\` — Copilot names the folder after the conversation/session GUID.
2. Fallback scan: reads `session.start` events in all folders looking for a `data.sessionId` match.

**Matching the `assistant.message` event:**

The same `interactionId` is reused for multiple turns within one interaction. Matching logic:

- If `turnId` is present: exact match on both `interactionId` **and** `turnId`. Stops at first match.
- If `turnId` is absent: last `assistant.message` with matching `interactionId` (backward-compatible fallback).

**Patching:**

- Only appends `input_tokens`, `output_tokens`, `cache_creation_tokens`, `cache_read_tokens` if the field is **not already present** (idempotent).
- Atomic write: write to `events.jsonl.tmp` then `File.Replace(tmp, eventsFile, null)`.

### `Console/Program.cs`
Cross-platform entry point. Starts the receiver, prints the startup line, then blocks on `await Task.Delay(Infinite, token)`. Cancellation token is set by:
- `Console.CancelKeyPress` (Ctrl+C — all platforms)
- `AppDomain.CurrentDomain.ProcessExit` (SIGTERM — Linux/macOS process managers, Docker)

On cancellation, `receiver.Stop()` is called in the `finally` block.

### `Windows/Program.cs`
Windows entry point (`OutputType=WinExe`). Attaches to the parent shell's console (or allocates a new one), re-opens `Console.Out`/`Console.Error` with UTF-8, then calls `Application.Run(new MainForm(port))`.

### `Windows/MainForm.cs`
A hidden `Form` (`ShowInTaskbar=false`, `Opacity=0`, `FormBorderStyle=None`) whose only purpose is to anchor the WinForms message loop and the `NotifyIcon` lifetime.

On load, starts the `OtlpHttpReceiver` and prints the startup line.

Tray context menu:
- **`Port: N`** — disabled label showing the active port.
- **`Show/Hide console`** — toggles the console window via `GetConsoleWindow` + `ShowWindow`. Checked state is refreshed from `IsWindowVisible` each time the menu opens.
- **`Run at startup`** — toggles `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` via `StartupManager`.
- **`Exit`** — stops the receiver and calls `Application.Exit()`.

### `Windows/StartupManager.cs`
Reads and writes `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` using `Microsoft.Win32.Registry`. The registry value is set to the full path of the running executable. Windows-only — not included in the Console project.

### `Windows/NativeMethods.cs`
P/Invoke declarations:
- `AllocConsole` / `AttachConsole` (kernel32) — console attachment.
- `GetConsoleWindow` / `ShowWindow` / `IsWindowVisible` (kernel32/user32) — console window visibility toggle.

---

## Key Design Decisions

| Decision | Rationale |
|---|---|
| `http://localhost:` not `http://+:` | Wildcard requires `netsh http add urlacl` with admin rights. `localhost` binding needs no permission. |
| Re-open `Console.OpenStandardOutput()` after `AttachConsole` | WinExe does not bind stdout/stderr automatically. Without this, all `Console.Write*` calls are no-ops. |
| Console app uses `CancellationTokenSource` + `Task.Delay(Infinite)` | Clean cross-platform shutdown: both Ctrl+C (`CancelKeyPress`) and SIGTERM (`ProcessExit`) cancel the token and unblock the await. |
| Core is a separate class library | Shared OTLP parsing, printing, and patching logic works identically on all platforms. Only the UI (tray vs. console loop) differs. |
| `File.Replace` for atomic patch | If the process crashes between write and close, the original file is intact. |
| `interactionId + turnId` matching | The same `interactionId` is reused for multiple turns; turnId disambiguates. Fallback to last-match preserves backward compatibility when turnId is absent. |
| First 8 chars of GUIDs in log | UUIDs are 36 chars; the first segment (8 hex chars) is distinctive enough for human reading while keeping lines short. |
| `Console.ForegroundColor` not ANSI codes | Works in all Windows consoles including legacy conhost without needing `ENABLE_VIRTUAL_TERMINAL_PROCESSING`. No visible characters when output is piped. |
| No `[tag]` prefix noise in STDIO | The structured key=value format is intended to be machine-parseable; brackets would interfere with simple tab-split parsing. |
