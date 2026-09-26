# acad-mcp-loop

面向 AutoCAD / AutoCAD Plant 3D 插件开发的 MCP 闭环调试引擎。

acad-mcp-loop 把整个迭代周期——**改代码 → 编译 → 热重载进运行中的 AutoCAD → 跑白名单命令 → 收日志 → 出 PASS/FAIL 判决**——折叠成一次 `run_iteration` 调用。任何支持 stdio MCP 的 AI agent（ZCode / Claude Desktop / Cursor / Cline / Continue 等）都能借此无人值守地迭代 CAD 插件，全程零 COM 自动化。

```
┌──────────┐  MCP stdio   ┌────────────┐  named pipe   ┌──────────┐
│ AI agent │ ───────────> │ LoopEngine │ ────────────> │ LoopHost │──> AutoCAD
│ (客户端)  │ <─────────── │   net8     │ <──────────── │  net472  │<── (NETLOAD)
└──────────┘  工具结果     └─────┬──────┘  JSON 消息     └────┬─────┘
                                 │ 盘日志 / 台账             │ 字节重载
                                 v                          v
                          ┌─────────────────────────────────────┐
                          │ 业务插件 DLL（示例 SamplePlugin）      │
                          └─────────────────────────────────────┘
```

两个核心组件配套发布、成对使用：

| 组件 | 运行时 | 职责 |
| ---- | ------ | ---- |
| `LoopEngine/` | .NET 8 控制台（stdio MCP server） | AI agent 直连的进程。负责编排编译、重载、命令执行、日志收集与 PASS/FAIL 判决，迭代台账落盘。 |
| `LoopHost/` | .NET Framework 4.7.2 类库 | NETLOAD 进 AutoCAD 的桥。持有命名管道服务、把作业泵到 CAD 主线程、不重启 CAD 字节重载业务插件、视图出图。 |
| `SamplePlugin/` | .NET Framework 4.7.2 | 集成契约的最小示例业务插件（白名单 `TEST_*`/`AGENT_*` 命令 + JSON 磁盘日志），让仓库开箱即可端到端跑通。 |

## 环境要求

- Windows（AutoCAD 插件；引擎同样仅 Windows）
- 能加载 .NET Framework 4.7.2 插件的 AutoCAD / Plant 3D（如 2019–2024）
- Visual Studio 2022 / Build Tools（MSBuild）+ .NET Framework 4.7.2 targeting pack —— 编 `LoopHost`、`SamplePlugin`
- .NET SDK 8+ —— 编 `LoopEngine`
- Autodesk 托管 DLL **不随仓库分发**（许可限制）。编译期从已安装的 AutoCAD 解析，或用 `-p:AcadRefs=<目录>` / 环境变量 `LOOP_ACAD_REFS` 指向 ObjectARX SDK 的 `inc` 目录。见 `AcadRefs.props`。

## 快速开始

1. **全量构建**

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```

2. **宿主装进 AutoCAD**：`NETLOAD` → 选 `LoopHost\bin\Debug\LoopHost.dll`。加载即自动启动（也可手动跑 `LOOPHOST` 命令）。就绪后命令行会打印管道名与 epoch。

3. **把 `loop.config.json.example` 复制为 `loop.config.json`，放两处**：`LoopEngine.exe` 旁（引擎读）与 `LoopHost.dll` 旁（宿主读）。按本机情况改 `paths.business_dll` / `acad_exe` / `drawing`。

4. **在 MCP 客户端注册 server**（见 `.mcp.json.example`）：

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

5. **跑第一轮闭环**（agent 侧，或任意 MCP inspector）：

   - `attach_cad` —— 拉起/挂接 AutoCAD 并确认宿主管道就绪
   - `run_iteration`：`topic: "smoke"`、`command: "TEST_ECHO"`、`expect: { "log_must_contain": ["TEST_ECHO PASS"] }` —— 引擎编译（可选）、重载业务 DLL、主线程跑命令、收集环日志 + 盘日志、返回 Tier-0 判决书。

   业务插件（`SamplePlugin`）在首次 `act` 类调用时自动装填——宿主比对磁盘 `BuildGuid` 边车与内存程序集，陈旧即字节重载。

## MCP 工具面

| 分组 | 工具 |
| ---- | ---- |
| attach | `attach_cad`（拉起/挂接 acad 并等宿主就绪）、`attach_reload`（热重载业务 DLL）、`attach_status`（状态机 + 二进制陈旧检测） |
| iterate | `run_iteration`（8 阶段 PREFLIGHT→REPORT 闭环，`run_id` 幂等）、`get_iteration`（判决 / 过滤日志尾读）、`list_iterations`（台账查询）、`diagnose`（失败根因诊断包）、`tail_log`（游标式日志查询） |
| act | `run_command`（执行白名单命令：`TEST_*` 裸名，`AGENT_*` 可带参） |
| query | `find_entities`、`get_entity`、`list_commands`、`get_scene_summary`、`get_workspace`、`list_documents`、`use_document`、`get_sysvars`、`get_screenshot`（高清视图出图，支持裁剪） |
| escape | `abort_cad`（acad 挂起时的安全中止：Esc PostMessage 兜底） |

## 配置

所有键可省略，括号内为缺省值。引擎键从 `LoopEngine.exe` 旁的 `loop.config.json` 读；宿主键从 `LoopHost.dll` 旁的同名文件读。

| 键 | 侧 | 含义 |
| -- | -- | ---- |
| `business_assembly` | 宿主 | （`"HotReloadCAD"`）业务插件程序集名——宿主字节重载的就是它，并从中反射注册类型 |
| `business_registration_type` | 宿主 | （`"HotReloadCAD.Agent.LoopToolRegistration"`）契约入口类的全名 |
| `shots_dir` | 宿主 | 截图默认目录（向上找到含 `build.ps1` 的仓库根则用其 `reports/agent-shots`，否则落 DLL 旁） |
| `paths.business_dll` | 引擎 | （`<根>/bin/x64/Debug/HotReloadCAD.dll`）待热重载的业务 DLL |
| `paths.host_dll` | 引擎 | 引擎期望的 `LoopHost.dll` 位置（部署校验用） |
| `build_script` | 引擎 | （根下 `build.ps1`，相对路径按根解析）`run_iteration{build:true}` 的编译入口 |
| `acad_exe` / `acad_args` | 引擎 | 要拉起的 AutoCAD（环境变量 `LOOP_ACAD_EXE` 优先；注册表探测兜底） |
| `drawing` | 引擎 | 默认/fixture 图纸（环境变量 `LOOP_DRAWING` 优先） |
| `disk_log_dir` | 引擎 | （`C:\Logs\Plant3D`）业务插件写 JSON 行日志的目录——判决的证据源 |
| `disk_log_file_prefix` | 引擎 | （`HotReloadCAD_`）文件名前缀：`<目录>\<前缀>yyyyMMdd.json` |
| `LOOP_ACAD_PID` | 引擎环境变量 | 多个 acad 实例时锁定其中一个 |
| `LOOP_DEBUG_DIR` | 宿主环境变量 | 覆盖宿主 `.debug` 暂存目录 |

引擎的**仓库根** = 从 `LoopEngine.exe` 向上最近的含 `build.ps1` 的祖先目录（找不到回落当前目录）——台账落在 `<根>/.debug/loop/iterations.jsonl`。

## 接入你自己的插件

详见 [`docs/plugin-contract.md`](docs/plugin-contract.md)。30 秒版：

1. 插件程序集暴露一个静态类，带 `Register(register, setTeardown, attachLogSink, claimSlot, releaseSlot)` 方法（3 参旧签名也可）。
2. 在 `Register` 里挂 `act.run_command` 处理器：只分发 `TEST_*`（裸名）/ `AGENT_*`（可带参）命令；关键日志行同时灌环形汇与 JSON 行磁盘日志（格式见契约文档）。
3. 每次构建嵌入 `AssemblyMetadata("BuildGuid", …)` 并把同值写入 `<dll>.buildguid`——陈旧检测与免重启重载靠它。
4. 把 `business_assembly` / `business_registration_type` / `paths.business_dll` 指向你的插件。

内部机制（管道协议、8 阶段迭代、世代与 epoch、图纸锁）见 [`docs/architecture.md`](docs/architecture.md)。

## 安全模型

- 只跑白名单命令：`TEST_*` 必须裸名调用；只有 `AGENT_*` 可带参数尾巴。其余一律 `TOOL_UNSAFE`。
- 按图纸互斥（`DRAWING_BUSY`）：两个 agent 不会并发迭代同一张 DWG。
- 宿主绝不拆 CAD：重载是同进程字节加载 + epoch 递增；acad 挂起时走 `abort_cad`（异步 Esc）逃生，文档化的最后手段才是重启。
- 业务异常不进管道层——一律转为 `ok:false` 载荷。

## 限制

- 仅 Windows；`LoopHost`/`SamplePlugin` 目标 net472（已跨过 net472 的 AutoCAD 版本需重定向这两个工程）。
- 引擎↔宿主管道名沿用历史值 `HotReloadCAD.LoopHost.v1`（作为线上协议常量保留）。
- 仓库暂不带独立测试；验证方式就是闭环本身（编译 → 重载 → 命令 → 判决）。

## 许可

[MIT](LICENSE)。Autodesk DLL 编译期从你本机的安装/SDK 解析，不属于本仓库的一部分。
