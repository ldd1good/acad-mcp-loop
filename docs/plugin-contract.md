# Business plugin contract

What your AutoCAD plugin must look like for LoopHost to hot-reload it and for LoopEngine to grade it. `SamplePlugin/` in this repo is a working reference for every point below.

## 1. Identity

| Item | Default | Configured by (host-side `loop.config.json`) |
| ---- | ------- | --------------------------------------------- |
| Assembly name | `HotReloadCAD` | `business_assembly` |
| Registration type full name | `HotReloadCAD.Agent.LoopToolRegistration` | `business_registration_type` |

The host locates the business generation by assembly **name** (not path) among loaded assemblies, then reflects the registration type out of it (`LoopHost/ReloadOrchestrator.cs`). When you rename via config, both keys must move together.

## 2. Registration entry

A public static class with a public static `Register` method. The host prefers the 5-argument signature and falls back to the 3-argument one:

```csharp
// preferred (generation slot claims enabled)
public static void Register(
    Action<string, Func<string, string>> register,   // mount a tool: name -> jsonIn -> jsonOut
    Action<Action> setTeardown,                      // register a teardown for this generation
    Func<Action<string>> attachLogSink,              // returns the host ring-log sink
    Action<string, string> claimSlot,                // claim a named generation slot
    Action<string> releaseSlot);                     // release it

// legacy fallback (no generation claims)
public static void Register(
    Action<string, Func<string, string>> register,
    Action<Action> setTeardown,
    Func<Action<string>> attachLogSink);
```

Semantics you must respect:

- `register("act.run_command", handler)` is the tool the engine's `run_command` / `run_iteration{command}` drives. You may mount more (`query.*` style read-only tools are a good fit).
- `setTeardown(action)`: before a new generation registers, the host runs the previous generation's teardown. Release caches, event subscriptions, unmanaged handles here.
- `attachLogSink()` returns an `Action<string>`. Push single-line JSON (e.g. `{"Level":"Information","Message":"TEST_X PASS"}`) into it; this is what `tail_log` and the fast COLLECT path see.
- **Attach the sink exactly once per generation.** A no-op reload re-runs `Register` for the same assembly; re-attaching duplicates every line into the ring (observed compounding to ×7 within a day in the originating project). Guard with a static flag — see `SamplePlugin/LoopToolRegistration.cs`.

## 3. `act.run_command` handler contract

Input JSON: `{"command": "NAME [arg tail]"}`. Return a JSON string; `ok:false` payloads are how business failures surface (never throw across the boundary — the host converts exceptions into FAIL jobs, but a clean `Fail(code, message)` keeps evidence tidy).

Whitelist discipline (the "AgentSafe" convention):

- command name must match `^[A-Za-z0-9_.\-]+$`;
- only names starting `TEST_` or `AGENT_` may be dispatched — anything else returns `TOOL_UNSAFE`;
- `TEST_*` must be invoked **bare** (no argument tail) so interactive/prompting commands can never be chained onto an unattended loop;
- only `AGENT_*` may receive a single space-separated argument tail.

A useful result payload:

```json
{ "ok": true, "build_guid": "<guid>", "synced": true, "command": "TEST_ECHO", "ms": 12 }
```

Including `build_guid` (read back your own `AssemblyMetadata`) lets the agent verify it just ran the generation it thinks it built.

## 4. Disk JSON log format

One JSON object per line, appended to `<disk_log_dir>/<disk_log_file_prefix>yyyyMMdd.json` (engine-side config; defaults `C:\Logs\Plant3D\HotReloadCAD_…`). The engine tails today + yesterday and filters out lines containing `"host":"` (pipe heartbeat noise). Field set the verdict logic relies on:

```json
{"Timestamp":"2026-09-23T13:57:41.354Z","Level":"Information","Message":"TEST_ECHO PASS ring+disk logging verified","Exception":null,"ProcessId":8616,"ThreadId":1,"Properties":{"CommandName":"TEST_ECHO"}}
```

- `Level` / `Message` are what `expect.log_must_contain` and `get_iteration{contains,level}` match against.
- Emit a machine-checkable **verdict line** (e.g. `TEST_ECHO PASS …`) from each whitelisted command — that is what makes `run_iteration` grade PASS.
- Avoid `Level:"Debug"` for anything load-bearing; treat per-frame probe spam carefully — it can push verdict lines out of the ring's collection window.

## 5. BuildGuid: staleness detection

Per build: embed `[assembly: AssemblyMetadata("BuildGuid", "<guid>")]` **and** write the same guid to `<your.dll>.buildguid` next to the DLL. `SamplePlugin.csproj` does both in one MSBuild step (`$([System.Guid]::NewGuid()…)`) — copy that pattern.

With this in place:

- the host refuses a reload whose disk bytes changed between the engine's read and its own (version/guid race guard);
- the engine auto-reloads a stale generation before `act` calls instead of letting you "verify" a fix that was never loaded;
- version checks compare `Major.Minor.Build` — bump versions per build if you also want run-time version evidence.

## 6. Compile-time references

Your plugin references the same three AutoCAD managed DLLs as `LoopHost` (`AcMgd` / `AcCoreMgd` / `AcDbMgd`, `Private=false`) — reuse `AcadRefs.props`. It must **not** reference `LoopHost` or `LoopEngine`; the contract is pure reflection and JSON strings, which is what keeps your DLL independently loadable and hot-reloadable.
