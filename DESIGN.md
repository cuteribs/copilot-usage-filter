# CopilotUsageFilter — Design Document

## Overview

CopilotUsageFilter is a minimal OpenTelemetry Collector implemented as a Windows system-tray application (.NET 10 WinForms, `OutputType=WinExe`). It listens for OTLP/HTTP traces exported by the GitHub Copilot CLI, extracts token-usage counts from each `chat` span, prints a structured log line to STDIO, and patches the matching `assistant.message` event in the Copilot session-state file.

---

## Architecture

```
Program.cs
  ├─ AttachConsole / AllocConsole  (kernel32)
  ├─ Console.SetOut / SetError     (UTF-8 re-bind)
  └─ Application.Run(MainForm)

MainForm (hidden WinForms window)
  ├─ OtlpHttpReceiver              (HttpListener on :4318)
  ├─ SpanProcessor                 (route → parse → log → patch)
  └─ NotifyIcon                    (system-tray icon + context menu)
        ├─ Show/Hide console toggle
        ├─ Run at startup toggle    (StartupManager → HKCU\Run)
        └─ Exit
```

### Data flow

```
Copilot CLI
  │  POST http://localhost:4318/v1/traces
  ▼
OtlpHttpReceiver.HandleRequestAsync
  │  (path, JSON body)
  ▼
SpanProcessor.Process
  ├─ Ignore /v1/metrics, /v1/logs  (silent return)
  ├─ OtlpTraceParser.ExtractChatSpans   ─► SpanAttributes[]
  │    └─ (fallback) SpanPayload.TryParse
  │
  ├─ PrintTokens  ──────────────────────────► STDIO colored log line
  └─ PatchSession
       └─ SessionStatePatcher.PatchAssistantMessage
            └─ events.jsonl (atomic replace)
```

---

## Component Descriptions

### `Program.cs`
Top-level entry point. Because the project is `OutputType=WinExe`, Windows does not connect stdout/stderr automatically even when launched from a terminal. The startup sequence is:

1. `AttachConsole(0xFFFFFFFF)` — attach to the parent process's console (the shell that launched us).
2. If attach fails, `AllocConsole()` — open a new console window.
3. Re-open `Console.OpenStandardOutput()` / `Console.OpenStandardError()` and call `Console.SetOut` / `Console.SetError` — necessary because WinExe never initialises these streams itself.
4. Force UTF-8 encoding on both streams.
5. Parse optional port argument (default 4318), then `Application.Run(new MainForm(port))`.

### `OtlpHttpReceiver.cs`
Wraps `HttpListener` on `http://localhost:{port}/`. Key decisions:

- **`http://localhost:`** not `http://+:` — wildcard prefixes require an elevated URL ACL reservation (`netsh http add urlacl`). Binding to `localhost` only needs no special permissions.
- Accepts POST and PUT (OTLP exporters use POST; some SDKs use PUT during negotiation).
- Returns `{}` with HTTP 200 on every request (standard OTLP success response).
- Fires `Func<string path, string body, Task>` callback asynchronously per request; the callback runs on a `Task.Run` thread to avoid blocking the listener loop.

### `OtlpTraceParser.cs`
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
- `intValue` may be a quoted string **or** a bare JSON number (OTLP spec quirk). Both are handled via try-catch on `GetValue<long>()` then `GetValue<string>()`.
- Only spans where `gen_ai.operation.name = "chat"` (case-insensitive) are returned; all others are discarded silently.
- Skips payloads that contain `resourceMetrics` or `resourceLogs` keys.

### `SpanPayload.cs` / `SpanAttributes`
Fallback deserialisation model for the simplified format:

```json
{ "type": "span", "attributes": { "gen_ai.operation.name": "chat", ... } }
```

This format is not emitted by the Copilot CLI but is supported for testing and future use.

`SpanAttributes` is also the shared carrier between both parsers and the rest of the pipeline.

### `SpanProcessor.cs`
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

### `SessionStatePatcher.cs`
Locates the session folder and patches `events.jsonl`.

**Finding the session folder:**

1. Direct: `%USERPROFILE%\.copilot\session-state\{conversationId}\` — Copilot names the folder after the conversation/session GUID.
2. Fallback scan: reads `session.start` events in all folders looking for a `data.sessionId` match (handles edge cases where the folder name differs from the conversation GUID reported in spans).

**Matching the `assistant.message` event:**

The same `interactionId` is reused for multiple turns within one interaction (e.g. turn 0 = user message, turn 1 = assistant reply). Matching logic:

- If `turnId` is present: exact match on both `interactionId` **and** `turnId`. Stops at first match.
- If `turnId` is absent: last `assistant.message` with matching `interactionId` (backward-compatible fallback).

`turnId` in `events.jsonl` may be stored as a JSON string `"0"` or a number `0`; both are handled.

**Patching:**

- Only appends `input_tokens`, `output_tokens`, `cache_creation_tokens`, `cache_read_tokens` if the field is **not already present** (idempotent).
- Atomic write: write to `events.jsonl.tmp` then `File.Replace(tmp, eventsFile, null)` — avoids corrupting the file if the process is killed mid-write.

### `MainForm.cs`
A hidden `Form` (`ShowInTaskbar=false`, `Opacity=0`, `FormBorderStyle=None`) whose only purpose is to anchor the WinForms message loop and the `NotifyIcon` lifetime.

On load, starts the `OtlpHttpReceiver` and prints the startup line:

```
<timestamp>    Listening    http://localhost:4318
```

Tray context menu:
- **`Port: N`** — disabled label showing the active port.
- **`Show/Hide console`** — toggles the console window via `GetConsoleWindow` + `ShowWindow`. Checked state is refreshed from `IsWindowVisible` each time the menu opens.
- **`Run at startup`** — toggles `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` via `StartupManager`.
- **`Exit`** — stops the receiver and calls `Application.Exit()`.

### `StartupManager.cs`
Reads and writes `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` using `Microsoft.Win32.Registry`. The registry value is set to the full path of the running executable.

### `NativeMethods.cs`
P/Invoke declarations:
- `AllocConsole` / `AttachConsole` (kernel32) — console attachment.
- `GetConsoleWindow` / `ShowWindow` / `IsWindowVisible` (kernel32/user32) — console window visibility toggle.

---

## Key Design Decisions

| Decision | Rationale |
|---|---|
| `http://localhost:` not `http://+:` | Wildcard requires `netsh http add urlacl` with admin rights. `localhost` binding needs no permission. |
| Re-open `Console.OpenStandardOutput()` after `AttachConsole` | WinExe does not bind stdout/stderr automatically. Without this, all `Console.Write*` calls are no-ops. |
| `File.Replace` for atomic patch | If the process crashes between write and close, the original file is intact. |
| `interactionId + turnId` matching | The same `interactionId` is reused for multiple turns; turnId disambiguates. Fallback to last-match preserves backward compatibility when turnId is absent. |
| First 8 chars of GUIDs in log | UUIDs are 36 chars; the first segment (8 hex chars) is distinctive enough for human reading while keeping lines short. |
| `Console.ForegroundColor` not ANSI codes | Works in all Windows consoles including legacy conhost without needing `ENABLE_VIRTUAL_TERMINAL_PROCESSING`. No visible characters when output is piped. |
| No `[tag]` prefix noise in STDIO | The structured key=value format is intended to be machine-parseable; brackets would interfere with simple tab-split parsing. |
