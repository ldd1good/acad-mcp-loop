using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace SamplePlugin
{
    /// <summary>
    /// 白名单命令实现（AgentSafe 约定的最小演示）：
    ///   · 命令名必须匹配 ^[A-Za-z0-9_.-]+$ 且以 TEST_ / AGENT_ 开头才可分发；
    ///   · TEST_* 必须裸名调用（防止把交互式命令挂上无人值守循环）；
    ///   · AGENT_* 允许一个空格分隔的参数尾巴。
    /// 每条命令同时写磁盘 JSON 日志（引擎 run_iteration 的证据源）与宿主环形日志
    /// （引擎 tail_log 的证据源），返回值为宿主回传给引擎的 JSON 串。
    /// </summary>
    public static class SampleCommands
    {
        /// <summary>宿主环形日志汇（Register 时由 LoopToolRegistration 注入）。</summary>
        internal static Action<string> RingSink;

        private static string BuildGuid()
        {
            try
            {
                foreach (var a in typeof(SampleCommands).Assembly.GetCustomAttributesData())
                {
                    if (a.AttributeType == null || a.AttributeType.Name != "AssemblyMetadataAttribute") continue;
                    if (a.ConstructorArguments.Count == 2 &&
                        (a.ConstructorArguments[0].Value as string) == "BuildGuid")
                        return a.ConstructorArguments[1].Value as string;
                }
            }
            catch { }
            return null;
        }

        private static void Log(string level, string message, IDictionary<string, object> props = null)
        {
            SampleLog.Write(level, message, props);
            var ring = RingSink;
            if (ring != null)
            {
                // 环形汇收单行 JSON（与磁盘行同构），引擎侧按行子串匹配
                try { ring(MiniJson.Write(new Dictionary<string, object>
                {
                    { "Level", level }, { "Message", message }
                })); } catch { }
            }
        }

        private static string Ok(Dictionary<string, object> extra)
        {
            var o = new Dictionary<string, object> { { "ok", true }, { "build_guid", BuildGuid() } };
            if (extra != null) foreach (var kv in extra) o[kv.Key] = kv.Value;
            return MiniJson.Write(o);
        }

        private static string Fail(string code, string message)
        {
            return MiniJson.Write(new Dictionary<string, object>
            {
                { "ok", false }, { "code", code }, { "message", message }
            });
        }

        // ------------------------------------------------------------------
        // 宿主工具面（LoopToolRegistration 注册给 ToolRegistry 的处理器）
        // ------------------------------------------------------------------

        public static string RunCommand(string jsonIn)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return Fail("NO_DOCUMENT", "no active document");

                var args = MiniJson.ParseObject(jsonIn ?? "{}") ?? new Dictionary<string, object>();
                var command = args.ContainsKey("command") ? args["command"] as string : null;
                if (string.IsNullOrWhiteSpace(command)) return Fail("BAD_ARGS", "command required");

                var trimmed = command.Trim();
                int sp = trimmed.IndexOf(' ');
                var name = sp < 0 ? trimmed : trimmed.Substring(0, sp);
                var argTail = sp < 0 ? string.Empty : trimmed.Substring(sp + 1).Trim();

                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_.\-]+$"))
                    return Fail("BAD_ARGS", "command name must be a bare token");
                if (!name.StartsWith("TEST_", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("AGENT_", StringComparison.OrdinalIgnoreCase))
                    return Fail("TOOL_UNSAFE", "'" + name + "' is not whitelisted (TEST_* bare / AGENT_* may take args)");
                if (argTail.Length > 0 && !name.StartsWith("AGENT_", StringComparison.OrdinalIgnoreCase))
                    return Fail("BAD_ARGS", "arguments are only allowed for AGENT_* commands");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string result;
                switch (name.ToUpperInvariant())
                {
                    case "TEST_ECHO":
                        result = CmdEcho();
                        break;
                    case "TEST_MODEL_INFO":
                        result = CmdModelInfo(doc);
                        break;
                    case "AGENT_DRAW_LINE":
                        result = CmdDrawLine(doc, argTail);
                        break;
                    default:
                        return Fail("TOOL_UNSAFE", "whitelist command '" + name + "' not found in current business generation");
                }
                sw.Stop();
                Log("Information", "命令 " + name + " 执行完成，耗时 " + sw.ElapsedMilliseconds + " 毫秒",
                    new Dictionary<string, object>
                    {
                        { "CommandName", name }, { "EventName", "CommandEnd" }, { "ExecutionTimeMs", sw.ElapsedMilliseconds }
                    });
                return result;
            }
            catch (System.Exception ex)
            {
                Log("Error", "命令执行异常: " + ex.Message);
                return Fail("COMMAND_FAILED", ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static string ListCommands(string jsonIn)
        {
            return MiniJson.Write(new Dictionary<string, object>
            {
                { "ok", true },
                { "commands", new List<object>
                {
                    new Dictionary<string, object> { { "name", "TEST_ECHO" }, { "whitelisted", true }, { "prompt_risk", false } },
                    new Dictionary<string, object> { { "name", "TEST_MODEL_INFO" }, { "whitelisted", true }, { "prompt_risk", false } },
                    new Dictionary<string, object> { { "name", "AGENT_DRAW_LINE" }, { "whitelisted", true }, { "prompt_risk", false } }
                } }
            });
        }

        // ------------------------------------------------------------------
        // 白名单命令本体
        // ------------------------------------------------------------------

        private static string CmdEcho()
        {
            Log("Information", "TEST_ECHO PASS ring+disk logging verified",
                new Dictionary<string, object> { { "CommandName", "TEST_ECHO" } });
            return Ok(new Dictionary<string, object>
            {
                { "synced", true }, { "command", "TEST_ECHO" }, { "echo", "pong" }
            });
        }

        private static string CmdModelInfo(Document doc)
        {
            var counts = new SortedDictionary<string, int>();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    var key = ent.GetType().Name;
                    counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
                }
                tr.Commit();
            }
            int total = 0;
            foreach (var kv in counts) total += kv.Value;
            Log("Information", "TEST_MODEL_INFO PASS entities=" + total,
                new Dictionary<string, object> { { "CommandName", "TEST_MODEL_INFO" }, { "Total", total } });
            var byType = new Dictionary<string, object>();
            foreach (var kv in counts) byType[kv.Key] = kv.Value;
            return Ok(new Dictionary<string, object>
            {
                { "synced", true }, { "command", "TEST_MODEL_INFO" }, { "total", total }, { "by_type", byType }
            });
        }

        private static string CmdDrawLine(Document doc, string argTail)
        {
            // 参数格式：AGENT_DRAW_LINE x1,y1,x2,y2（模型空间坐标，图纸单位）
            var parts = (argTail ?? "").Split(',');
            double x1, y1, x2, y2;
            if (parts.Length != 4 ||
                !double.TryParse(parts[0], out x1) || !double.TryParse(parts[1], out y1) ||
                !double.TryParse(parts[2], out x2) || !double.TryParse(parts[3], out y2))
                return Fail("BAD_ARGS", "usage: AGENT_DRAW_LINE x1,y1,x2,y2");

            ObjectId lineId;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var line = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
                lineId = ms.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
                tr.Commit();
            }
            Log("Information", "AGENT_DRAW_LINE PASS line created",
                new Dictionary<string, object>
                {
                    { "CommandName", "AGENT_DRAW_LINE" }, { "Handle", lineId.Handle.ToString() }
                });
            return Ok(new Dictionary<string, object>
            {
                { "synced", true }, { "command", "AGENT_DRAW_LINE" }, { "handle", lineId.Handle.ToString() }
            });
        }
    }
}
