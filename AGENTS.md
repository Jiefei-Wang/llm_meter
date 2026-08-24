# LLM Meter Project Guide

This file is the working contract for agents modifying this repository. Read it before making changes. Preserve unrelated worktree changes and keep changes narrowly scoped.

## Product

LLM Meter is a lightweight, privacy-preserving Windows tray application with small frameless WPF widgets for monitoring local LLM inference servers in real time. It supports llama.cpp/llama-server, vLLM, LM Studio, Ollama, and generic OpenAI-compatible endpoints.

The application must remain:

- Local-only: never upload telemetry, prompts, completions, configuration, or discovered endpoints.
- Passive: use read-only monitoring endpoints and do not submit inference requests to manufacture metrics.
- Honest: distinguish native, derived/approximate, and unavailable values. Never invent unsupported telemetry.
- Lightweight: share one polling stream per endpoint and avoid blocking or redundant HTTP requests.
- Stable on screen: live values must not cause the widget to change width, jump, or reorder active-request rows unexpectedly.

## Architecture

- `src/LLMMeter/Core`: immutable snapshots, metric quality/provenance, formatting, endpoint/backend models, and HTTP abstractions.
- `src/LLMMeter/Adapters`: backend fingerprinting and telemetry collection. Optional endpoint failures must not throw out otherwise valid telemetry.
- `src/LLMMeter/Collection`: shared polling, rate calculations, rolling metrics, and collector lifecycle.
- `src/LLMMeter/Discovery`: Windows, WSL, port, and backend discovery/fingerprinting.
- `src/LLMMeter/Persistence`: configuration and window-state persistence.
- `src/LLMMeter/UI`: WPF windows, request rows, activity charts, tray behavior, themes, and view models.
- `tests/LLMMeter.Tests`: unit and regression tests. Add coverage for telemetry schema or formatting changes.

Use the existing `IHttp` abstraction in adapters so behavior remains testable. Keep `MetricSnapshot` immutable after publication. UI code consumes snapshots and should not perform backend HTTP calls.

## Telemetry rules

Every metric has a `MetricQuality` and `MetricSource`:

- `Exact`: directly reported by a native endpoint.
- `Approximate`: derived from deltas, smoothing, or another defensible calculation.
- `Unavailable`: show an em dash; do not substitute zero.

Rates must use monotonic time, handle counter resets, and never report negative throughput. Do not confuse server-wide cumulative counters with per-request values.

### llama.cpp

Poll `/metrics` and `/slots` concurrently. `/metrics` supplies aggregate counters/gauges; `/slots` supplies active-request details. Enabling `--metrics` must not disable request enumeration or noticeably serialize the polling path.

Important `/slots` semantics:

- `n_prompt_tokens_cache`: prompt tokens reused from KV cache.
- `n_prompt_tokens_processed`: prompt tokens evaluated for the current request.
- `n_prompt_tokens`: a progressively populated slot prompt in current llama-server versions; it is not reliably the final submitted input size at the beginning of prefill.
- For the per-request `IN` value, prefer `CACHED + EVAL` when both native fields exist. Fall back to `n_prompt_tokens` for older schemas.
- `n_decoded` is the per-request output token count. It may reset when a slot receives a new task; re-baseline rather than generating a negative rate.

The active-request statistics line is intentionally compact and stable:

```text
IN  12.32k·CACHED   512·EVAL 11.81k·OUT    203·61.4/s
```

Requirements for this line:

- Labels are `IN`, `CACHED`, `EVAL`, and `OUT`.
- Each value, including speed, reserves six monospace characters and is left-padded when shorter.
- Use compact `·` separators without surrounding spaces.
- Do not show the approximate `~` prefix on per-request speed.
- Keep a constant font size. Do not use a `Viewbox` or dynamic font scaling.
- Constrain request cards to the metrics-grid/widget width. Long text may use trimming only as a final safety measure; it must never widen the window.
- Keep request rows stable by task ID. Completed rows linger briefly and empty interior slots are reused before appending rows.

## Window behavior

The widget uses `SizeToContent` and a layout scale transform. New content can accidentally alter desired width, so constrain dynamic sections explicitly. Opening charts, expanding details, changing counters, or adding request rows must not change the widget width. Vertical request-list height may grow to the configured high-water mark and is user-resizable.

Preserve per-monitor DPI behavior, frameless dragging, edge scaling, topmost behavior, and saved placement. Avoid blocking work on the WPF dispatcher.

## Discovery and privacy

Discovery may inspect local TCP listeners, known localhost ports, process metadata, and WSL listeners. Fingerprint by endpoint response shape or namespaced metric families, not by port alone. `/v1/models` alone is insufficient to distinguish llama.cpp, Ollama, and generic OpenAI-compatible servers.

Never store or display prompt/completion content. `/slots` must be queried in metrics-only form as supported by the server, and only numeric/status metadata should enter snapshots.

## Verification

Use the .NET 8 SDK. This machine may have only the runtime on `PATH`; the repository-local SDK is at:

```powershell
.\.tools\dotnet\dotnet.exe
```

Run the complete suite after code or XAML changes:

```powershell
& '.\.tools\dotnet\dotnet.exe' test tests\LLMMeter.Tests\LLMMeter.Tests.csproj --no-restore
git diff --check
```

Do not claim tests passed unless the command completed successfully. Current baseline is 102 tests; the number may increase as regressions are added.

## Release output

The requested user-facing artifact is a self-contained, single-file Windows x64 executable at:

```text
publish\LLMMeter.exe
```

Publish after successful tests:

```powershell
& '.\.tools\dotnet\dotnet.exe' publish src\LLMMeter\LLMMeter.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish --no-restore
```

The user may be running the Release executable, which can lock `src\LLMMeter\bin\Release\...\LLMMeter.exe`. Do not terminate the live widget merely to build. Route intermediates elsewhere:

```powershell
& '.\.tools\dotnet\dotnet.exe' publish src\LLMMeter\LLMMeter.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:BaseOutputPath="$PWD\.tools\build\" `
  -o publish --no-restore
```

Confirm the resulting file timestamp and report a clickable link to `publish/LLMMeter.exe`. Remind the user that an already-running widget must be restarted to load a new build.

