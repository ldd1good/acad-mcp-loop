# acad-mcp-loop

An MCP-driven closed feedback loop for AutoCAD / AutoCAD Plant 3D plugin development.

acad-mcp-loop folds the whole cycle — **edit code → build → hot-reload into a running AutoCAD → run a whitelisted command → collect logs → get a PASS/FAIL verdict** — into a single `run_iteration` call, so any stdio-MCP AI agent (ZCode, Claude Desktop, Cursor, Cline, Continue, …) can iterate on a CAD plugin unattended, with zero COM automation.

```
┌──────────┐  MCP stdio   ┌────────────┐  named pipe   ┌──────────┐
│ AI agent │ ───────────> │ LoopEngine │ ────────────> │ LoopHost │──> AutoCAD
│ (client) │ <─────────── │   net8     │ <──────────── │  net472  │<── (NETLOAD)
└──────────┘  tool result └─────┬──────┘  JSON messages└────┬─────┘
                                 │ disk logs / ledger       │ byte reload
                                 v                          v
                          ┌─────────────────────────────────────┐
                          │ business plugin DLL (SamplePlugin)  │
                          └─────────────────────────────────────┘
```

Two components ship together and are always used as a pair:

| Component | Runtime | Role |
| --------- | ------- | ---- |
| `LoopEngine/` | .NET 8 console (stdio MCP server) | The process your AI agent talks to. Orchestrates build, reload, command execution, log collection, and PASS/FAIL verdicts. Keeps the iteration ledger on disk. |
| `LoopHost/` | .NET Framework 4.7.2 class library | The bridge loaded into AutoCAD via `NETLOAD`. Owns the named-pipe server, pumps jobs onto AutoCAD's main thread, byte-reloads your business plugin without restarting CAD, captures screenshots. |
| `SamplePlugin/` | .NET Framework 4.7.2 | A minimal example business plugin implementing the integration contract (whitelisted `TEST_*`/`AGENT_*` commands + JSON disk logging), so the repo works end-to-end out of the box. |

## Requirements

- Windows (AutoCAD plugins; the engine is Windows-only too)
- AutoCAD or AutoCAD Plant 3D that loads .NET Framework 4.7.2 plugins (e.g. 2019–2024)
- Visual Studio 2022 / Build Tools (MSBuild) + .NET Framework 4.7.2 targeting pack — for `LoopHost` and `SamplePlugin`
- .NET SDK 8+ — for `LoopEngine`
- Autodesk managed DLLs are **not** redistributed here (license). They are resolved at compile time from an installed AutoCAD, or via `-p:AcadRefs=<dir>` / env `LOOP_ACAD_REFS` pointing at an ObjectARX SDK `inc` folder. See `AcadRefs.props`.

## Quick start

1. **Build everything**

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```

2. **Load the host into AutoCAD**: `NETLOAD` → pick `LoopHost\bin\Debug\LoopHost.dll`. It starts automatically on load (or run the `LOOPHOST` command). The editor prints the pipe name and epoch when ready.

3. **Copy `loop.config.json.example` to `loop.config.json`** in *two* places: next to `LoopEngine.exe` (the engine reads it) and next to `LoopHost.dll` (the host reads it). Adjust `paths.business_dll` / `acad_exe` / `drawing` for your machine.

4. **Register the MCP server** in your client (see `.mcp.json.example`):

   ```json
   {
     "mcpServers": {
       "acad-mcp-loop": {
         "type": "stdio",
         "command": "C:\\...\\LoopEngine\\bin\\Debug\\net8.0\\LoopEngine.exe",
         "args": ["--serve"],
         "env": {
           "LOOP_ACAD_EXE": "C:\\Program Files\\Autodesk\\AutoCAD 2024\\acad.exe",
           "LOOP_DRAWING": "C:\\path\\to\\fixture.dwg"
         },
         "timeout": 60000
       }
     }
   }
   ```

5. **Run your first loop** (agent-side, or any MCP inspector):

   - `attach_cad` — starts/attaches AutoCAD and verifies the host pipe
   - `run_iteration` with `topic: "smoke"`, `command: "TEST_ECHO"` and `expect: { "log_must_contain": ["TEST_ECHO PASS"] }` — the engine builds (optional), reloads the business DLL, runs the command on the main thread, collects ring + disk logs, and returns a Tier-0 verdict.

   The business plugin (`SamplePlugin`) is loaded automatically on the first `act`-family call — the host compares the on-disk `BuildGuid` sidecar with the in-memory assembly and byte-reloads when stale.

## MCP tool surface

| Group | Tools |
| ----- | ----- |
| attach | `attach_cad` (launch/attach acad + wait for host), `attach_reload` (hot-reload business DLL), `attach_status` (state machine + stale-binary detection) |
| iterate | `run_iteration` (8-phase PREFLIGHT→REPORT loop, idempotent by `run_id`), `get_iteration` (verdict / filtered log tail), `list_iterations` (ledger query), `diagnose` (failure root-cause pack), `tail_log` (cursor-based log query) |
| act | `run_command` (run an `[AgentSafe]`-whitelisted command: `TEST_*` bare, `AGENT_*` may take args) |
| query | `find_entities`, `get_entity`, `list_commands`, `get_scene_summary`, `get_workspace`, `list_documents`, `use_document`, `get_sysvars`, `get_screenshot` (high-res view capture with crop options) |
| escape | `abort_cad` (safe abort: Esc PostMessage fallback when acad is hung) |

## Configuration

All keys optional; defaults in parentheses. Engine keys are read from `loop.config.json` next to `LoopEngine.exe`; host keys from `loop.config.json` next to `LoopHost.dll`.

| Key | Side | Meaning |
| --- | ---- | ------- |
| `business_assembly` | host | (`"HotReloadCAD"`) assembly name of your business plugin — the host byte-reloads *this* assembly and reflects the registration type out of it |
| `business_registration_type` | host | (`"HotReloadCAD.Agent.LoopToolRegistration"`) full type name of the contract entry class |
| `shots_dir` | host | default screenshot dir (repo `reports/agent-shots` if a `build.ps1` root is found walking up, else beside the DLL) |
| `paths.business_dll` | engine | (`<root>/bin/x64/Debug/HotReloadCAD.dll`) business DLL to hot-reload |
| `paths.host_dll` | engine | where the engine expects `LoopHost.dll` (deploy checks) |
| `build_script` | engine | (`build.ps1` at repo root; relative paths resolve against the root) what `run_iteration{build:true}` invokes |
| `acad_exe` / `acad_args` | engine | AutoCAD executable to launch (env `LOOP_ACAD_EXE` wins; registry probe as fallback) |
| `drawing` | engine | default/fixture drawing (env `LOOP_DRAWING` wins) |
| `disk_log_dir` | engine | (`C:\Logs\Plant3D`) where your plugin writes JSON-lines logs — this is the evidence source for verdicts |
| `disk_log_file_prefix` | engine | (`HotReloadCAD_`) file-name prefix: `<dir>\<prefix>yyyyMMdd.json` |
| `LOOP_ACAD_PID` | engine env | pin one acad instance when several are running |
| `LOOP_DEBUG_DIR` | host env | override the host's `.debug` scratch dir |

The engine resolves its **repo root** as the nearest ancestor of `LoopEngine.exe` containing `build.ps1` (falling back to the current directory) — the ledger lives at `<root>/.debug/loop/iterations.jsonl`.

## Integrating your own plugin

Read [`docs/plugin-contract.md`](docs/plugin-contract.md). The 30-second version:

1. Your plugin assembly exposes a static class with a `Register(register, setTeardown, attachLogSink, claimSlot, releaseSlot)` method (a 3-arg legacy signature also works).
2. Inside `Register`, mount an `act.run_command` handler that only dispatches `TEST_*` (bare) / `AGENT_*` (may take args) commands, and push your key log lines into the ring sink *and* a JSON-lines disk log matching the format in the contract doc.
3. Embed `AssemblyMetadata("BuildGuid", …)` per build and write the same value to `<dll>.buildguid` — that's what makes stale-detection and no-restart reloads work.
4. Point `business_assembly` / `business_registration_type` / `paths.business_dll` at your plugin.

Internals (pipe protocol, 8-phase iteration, generations & epochs, drawing lock) are documented in [`docs/architecture.md`](docs/architecture.md).

## Safety model

- Only whitelisted commands run: `TEST_*` must be invoked bare; only `AGENT_*` may receive an argument tail. Anything else returns `TOOL_UNSAFE`.
- A per-drawing mutex (`DRAWING_BUSY`) stops two agents from iterating on the same DWG concurrently.
- The host never tears down CAD: reloads are byte-loads into the same process with epoch bumping; a hung acad is escaped via `abort_cad` (async Esc), not process kills, and only escalates to restart as a documented last resort.
- Business exceptions never propagate into the pipe layer — they become `ok:false` payloads.

## Limitations

- Windows-only; `LoopHost`/`SamplePlugin` target .NET Framework 4.7.2 (AutoCAD versions that moved past net472 need a retarget of those two projects).
- The engine↔host pipe name is the historical `HotReloadCAD.LoopHost.v1` (kept as the stable wire constant).
- No tests ship in this repo yet; verification is the loop itself (build → reload → command → verdict).

## License

[MIT](LICENSE). Autodesk DLLs are referenced at build time from your own installation/SDK and are not part of this repository.
