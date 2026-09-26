# Architecture

This document describes what is actually in the source. Everything below can be located in the code; file references are given per section.

## Process topology

```
┌──────────┐  MCP stdio   ┌────────────┐  named pipe              ┌──────────┐
│ AI agent │ ───────────> │ LoopEngine │ ───────────────────────> │ LoopHost │
└──────────┘  (JSON-RPC)  │  net8 exe  │  HotReloadCAD.LoopHost.v1│  net472  │
                           └─────┬──────┘  (JSON request/response) └────┬─────┘
                                 │                                     │
        ┌────────────────────────┼─────────────────────┐               │
        v                        v                     v               v
  <root>/.debug/loop/     disk JSON logs        powershell       byte reload +
  iterations.jsonl        (business plugin)     build_script     epoch bump
  <root>/.debug/runs/
```

- **LoopEngine** (`LoopEngine/Program.cs`) is a stdio MCP server: one JSON-RPC line in → one line out, responses written under a lock; `stdout` is silenced and crash diagnostics go to `<root>/.debug/loop/engine-<pid>.log`.
- **LoopHost** (`LoopHost/PipeServer.cs`) is an `IExtensionApplication` library loaded with `NETLOAD`. It starts on the first Idle event after load (or the `LOOPHOST` command) and serves a named pipe. The pipe name is the historical constant `HotReloadCAD.LoopHost.v1` — it is the wire contract between the two processes and is deliberately kept stable.
- The engine resolves its **repo root** as the nearest ancestor of `LoopEngine.exe` containing `build.ps1`, falling back to the current directory (`LoopEngine/Repo.cs`). The iteration ledger lives at `<root>/.debug/loop/iterations.jsonl`, per-run artifacts under `<root>/.debug/runs/`.

## The loop: `run_iteration` phases

One call walks the pipeline PREFLIGHT → BUILD → RELOAD → ARRANGE → COMMAND → COLLECT → REPORT and appends a terminal DONE record (`LoopEngine/Iteration.cs`, stage names literal in source):

| Phase | What happens |
| ----- | ------------ |
| PREFLIGHT | drawing lock acquisition (`DrawingLock.cs`, per-drawing mutex named after the DWG; contention yields `DRAWING_BUSY` with an `expected_release_at` estimate), fixture resolution |
| BUILD | optional (`build:true`): runs the `build_script` via PowerShell (`BuildRunner.cs`); failures become `BUILD_FAILED` with `build_errors` / `build_tail` evidence |
| RELOAD | byte-reload of the business DLL through the host (below) |
| ARRANGE | `fixture` open (or activate-if-open) with the drawing lock following the document |
| COMMAND | whitelisted command executed on the CAD main thread |
| COLLECT | ring log + disk JSON log collection with a quiet-window; `"host":"…"` pipe-noise lines are filtered; a hard `collect_tail_deadline_ms` ceiling stops silent hangs |
| REPORT | Tier-0 verdict book (≤ 2 KB): verdict / phase / code / evidence lines |

Additional semantics, all literal in source or tool contracts:

- **Idempotent `run_id`**: replaying a finished run returns its terminal state; calling while it runs blocks-and-continues instead of double-running.
- **`shadow:true`** double-runs the iteration and reports a consistency field (verdict-stability check).
- **Verdict rules**: `expect.log_must_contain` etc.; shadow mismatch downgrades with a hint; blacklisted exceptions (eDuplicateKey / eLockViolation / MissingMethod …) are treated as fatal code problems with a fixed remediation order: fix → build → reload → re-run.

## Hot reload without restarting CAD

`LoopHost/AssemblyDependent.cs` + `ReloadOrchestrator.cs`:

1. The host reads the business DLL **from bytes** (plus dependency DLLs), so the file on disk stays writable for the next build.
2. Before every reload the host validates disk vs expected `Major.Minor.Build` version and `BuildGuid` (embedded `AssemblyMetadata("BuildGuid", …)` + `<dll>.buildguid` sidecar); a mismatch mid-flight aborts the prepare.
3. The freshly loaded generation's static `LoopToolRegistration.Register(...)` is invoked (5-arg signature preferred, 3-arg legacy fallback) and the tool **epoch** advances; ring-log consumers can tell generations apart.
4. Staleness is also checked *opportunistically*: before `act` calls the engine compares the disk sidecar against the in-memory BuildGuid and reloads automatically when they differ — the "verified on a stale DLL" failure mode this guards against is documented in the source comments.
5. The same trick protects against skipped builds: `attach_status` reports a stale-infrastructure block (source newer than deployed binary, `STALE-SKIP.marker`).

## Main-thread execution model

AutoCAD's APIs are main-thread-bound. `LoopHost/MainThreadPump.cs` posts closures to the main thread; command jobs run there, optionally under a document lock (`LoopHost/CommandJobs.cs`):

- synchronous `act` tools occupy a single mutual-exclusion slot — one CAD-touching call at a time;
- async jobs (`act.run_command_async` → `job_id`) keep at most one RUNNING job, remember the last 16, and are pollable slot-free via `act.job_status`;
- read-only polling tools (status, job status, abort) are slot-free by design so they can never be locked out by the very call they supervise;
- if the main thread stalls, the engine surfaces `CAD_HUNG` / `MAIN_THREAD_STALLED` and `abort_cad` posts an async Esc (+ command-line `\x03`) to the acad window — recovery first, process restart only as a documented last resort (`LoopEngine/AcadEscAbort.cs`, `LoopHost/CadAbortTool.cs`).

## Evidence chain

- **Ring log** (`LoopHost/RingLogSink.cs`): bounded in-memory buffer the business plugin writes into via the sink handed to `Register`. Powers `tail_log` and the fast COLLECT path. Business plugins must attach their sink exactly once per generation or every line gets duplicated (the sample guards this with a static flag).
- **Disk JSON logs**: the business plugin appends one-JSON-per-line records (format in `plugin-contract.md`). The engine tails today's and yesterday's file from `disk_log_dir`, filters host-pipe noise, and evaluates `expect` rules against these lines — disk is the durable source when the ring wraps.
- **Ledger** (`LoopEngine/Ledger.cs`): JSONL stage records per run; `list_iterations` aggregates verdict counts per topic; `diagnose` joins the ledger with recent runs into a root-cause pack.

## Screenshots

`LoopHost/ScreenshotTool.cs` uses `Document.CapturePreviewImage` (not the PLOT pipeline): width is recomputed from height at a locked aspect, with center / entity-bbox (`crop_entity_prefix`, xdata-keyed) / paper-space-mm crop modes. It never changes view state.
