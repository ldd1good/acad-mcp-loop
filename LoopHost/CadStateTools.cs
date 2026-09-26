using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace LoopHost
{

    public static class CadStateTools
    {
        public static readonly string[] ToolNames =
        {
            "query.sysvars", "query.documents", "query.workspace", "query.scene_summary", "use_document",
            "query.screenshot", "query.viewports"
        };

        private const int SceneSummaryEntityCap = 200000;
        private const int TopLayers = 20;
        private const int MaxSysvarBatch = 32;

        public static bool TryGet(string name, out Func<string, string> handler)
        {
            switch (name)
            {
                case "query.sysvars": handler = GetSysvars; return true;
                case "query.documents": handler = ListDocuments; return true;
                case "query.workspace": handler = GetWorkspace; return true;
                case "query.scene_summary": handler = GetSceneSummary; return true;
                case "use_document": handler = UseDocument; return true;
                case "query.screenshot": handler = ScreenshotTool.Handle; return true;
                case "query.viewports": handler = GetViewports; return true;
                default: handler = null; return false;
            }
        }

        private static string GetSysvars(string jsonIn)
        {
            var payload = MiniJson.ParseObject(jsonIn) ?? new Dictionary<string, object>();
            var names = new List<string>();
            var rawList = payload.ContainsKey("names") ? payload["names"] as List<object> : null;
            if (rawList != null)
            {
                foreach (var item in rawList)
                {
                    var s = item as string;
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
                }
            }
            else
            {
                var single = payload.ContainsKey("name") ? payload["name"] as string : null;
                if (!string.IsNullOrWhiteSpace(single)) names.Add(single.Trim());
            }
            if (names.Count == 0)
                return Fail("BAD_ARGS", "names (array) or name (string) required");
            if (names.Count > MaxSysvarBatch)
                return Fail("BAD_ARGS", "max " + MaxSysvarBatch + " sysvars per call");

            var values = new Dictionary<string, object>();
            var errors = new Dictionary<string, object>();
            foreach (var name in names)
            {
                try
                {
                    var v = Application.GetSystemVariable(name);
                    object json;
                    if (v == null || v is DBNull) json = null;
                    else if (v is short || v is int || v is long || v is byte) json = Convert.ToInt64(v);
                    else if (v is double || v is float) json = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    else if (v is bool) json = (bool)v;
                    else json = v.ToString();
                    values[name] = json;
                }
                catch (Exception ex)
                {
                    errors[name] = ex.GetType().Name;
                }
            }
            return Ok(new Dictionary<string, object>
            {
                { "values", values },
                { "errors", errors }
            });
        }

        private static string ListDocuments(string jsonIn)
        {
            var docs = new List<object>();
            var active = ActiveDoc();
            foreach (Document doc in Application.DocumentManager)
            {
                if (doc == null) continue;
                docs.Add(DocumentInfo(doc, active, doc == active));
            }
            return Ok(new Dictionary<string, object>
            {
                { "count", docs.Count },
                { "documents", docs }
            });
        }

        private static Dictionary<string, object> DocumentInfo(Document doc, Document active, bool isActive)
        {
            string path = null;
            bool unnamed = false;
            try
            {
                path = doc.Database.Filename;
                var dir = Path.GetDirectoryName(path);
                unnamed = string.IsNullOrEmpty(dir);
            }
            catch { }
            bool readOnly = false;
            try { readOnly = doc.IsReadOnly; } catch { }
            return new Dictionary<string, object>
            {
                { "name", SafeName(doc) },
                { "path", path },
                { "unnamed", unnamed },
                { "active", isActive },
                { "read_only", readOnly }
            };
        }

        private static string SafeName(Document doc)
        {
            try { return doc.Name; }
            catch { return "?"; }
        }

        private static Document ActiveDoc()
        {
            try { return Application.DocumentManager.MdiActiveDocument; }
            catch { return null; }
        }

        /// <summary>query.viewports（2026-09-19，目标：消灭"为看视口账写业务探针命令"）：
        /// 活动布局的视口清单（号/纸面矩形/on/locked/比例/视图方向/所在图层）+ 布局打印信息
        /// （纸宽高/原点/旋转）+ 旋转感知的图幅内判定。经验来源：本日排障坐实"幅外视口的
        /// 纸面标注不渲染"（A1 rot90 纸面有效区=x[0,841]×y[0,594]，前视#3/左视#4 幅外），
        /// 此前 agent 只能现写 TEST_VPLIST/TEST_VPDIAG 业务命令取证。</summary>
        private static string GetViewports(string jsonIn)
        {
            var doc = ActiveDoc();
            if (doc == null) return Fail("NO_DOCUMENT", "no active document");
            var db = doc.Database;
            var vps = new List<object>();
            string layoutName = null;
            var sheet = new Dictionary<string, object>();
            bool sheetKnown = false;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lm = Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current;
                    layoutName = lm.CurrentLayout;
                    ObjectId layId = lm.GetLayoutId(layoutName);

                    double sx0 = 0, sy0 = 0, sx1 = 0, sy1 = 0;
                    if (!layId.IsNull)
                    {
                        var lay = tr.GetObject(layId, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Layout;
                        if (lay != null)
                        {
                            var psz = lay.PlotPaperSize;   // (宽mm, 高mm)（未旋转口径）
                            var po = lay.PlotOrigin;
                            bool rotSwap = lay.PlotRotation == Autodesk.AutoCAD.DatabaseServices.PlotRotation.Degrees090
                                        || lay.PlotRotation == Autodesk.AutoCAD.DatabaseServices.PlotRotation.Degrees270;
                            double sheetW = rotSwap ? psz.Y : psz.X;
                            double sheetH = rotSwap ? psz.X : psz.Y;
                            sx0 = po.X; sy0 = po.Y; sx1 = po.X + sheetW; sy1 = po.Y + sheetH;
                            sheetKnown = true;
                            sheet["paper_mm"] = string.Format(CultureInfo.InvariantCulture,
                                "{0:F1}x{1:F1}", psz.X, psz.Y);
                            sheet["origin"] = string.Format(CultureInfo.InvariantCulture,
                                "({0:F1},{1:F1})", po.X, po.Y);
                            sheet["rotation"] = lay.PlotRotation.ToString();
                            sheet["sheet_in_paper_space"] = string.Format(CultureInfo.InvariantCulture,
                                "({0:F0},{1:F0})-({2:F0},{3:F0})", sx0, sy0, sx1, sy1);
                            sheet["note"] = "纸面实体（标注）在 sheet_in_paper_space 之外不渲染不打印";
                        }
                    }

                    var psId = db.CurrentSpaceId;
                    // 模型空间态给出空清单 + 明示（视口是图纸空间对象）
                    int tilemodeInt;
                    try { tilemodeInt = Convert.ToInt32(Application.GetSystemVariable("TILEMODE")); }
                    catch { tilemodeInt = 1; }
                    if (tilemodeInt != 0)
                    {
                        tr.Commit();
                        return Ok(new Dictionary<string, object>
                        {
                            { "layout", layoutName }, { "tilemode", 1 },
                            { "viewports", vps },
                            { "note", "TILEMODE=1（模型选项卡）——视口属于图纸空间，无账可列" }
                        });
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    // 活动布局的 paperspace（CurrentSpaceId 在图纸空间时即其 BTR；在 MS-in-VP 时
                    // 为模型空间——退回 *Paper_Space 常量键不可靠，按布局 id 取 BTR 稳妥）
                    if (!layId.IsNull)
                    {
                        var lay2 = tr.GetObject(layId, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Layout;
                        if (lay2 != null) psId = lay2.BlockTableRecordId;
                    }
                    if (psId.IsNull)
                    {
                        tr.Commit();
                        return Fail("NO_PAPERSPACE", "cannot resolve paperspace of layout " + layoutName);
                    }
                    var ps = (BlockTableRecord)tr.GetObject(psId, OpenMode.ForRead);
                    foreach (ObjectId id in ps)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                        var e = vp.GeometricExtents;
                        string shade = "?";
                        try { shade = vp.ShadePlot.ToString(); } catch { }
                        string viewDir = "?";
                        try
                        {
                            var d = vp.ViewDirection;
                            viewDir = string.Format(CultureInfo.InvariantCulture, "{0:F0},{1:F0},{2:F0}", d.X, d.Y, d.Z);
                        }
                        catch { }
                        string twist = null;
                        try { twist = vp.TwistAngle.ToString("F3", CultureInfo.InvariantCulture); } catch { }
                        bool offSheet = sheetKnown &&
                            (e.MinPoint.X < sx0 - 1 || e.MaxPoint.X > sx1 + 1 ||
                             e.MinPoint.Y < sy0 - 1 || e.MaxPoint.Y > sy1 + 1);
                        var item = new Dictionary<string, object>
                        {
                            { "number", vp.Number },
                            { "on", vp.On },
                            { "locked", vp.Locked },
                            { "layer", vp.Layer },
                            { "paper", string.Format(CultureInfo.InvariantCulture,
                                "({0:F1},{1:F1})-({2:F1},{3:F1})",
                                e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y) },
                            { "custom_scale", vp.CustomScale },
                            { "shade_plot", shade },
                            { "view_direction", viewDir }
                        };
                        if (twist != null) item["twist"] = twist;
                        if (offSheet)
                        {
                            item["off_sheet"] = true;
                            item["warning"] = "视口在图幅外：其纸面标注/图框内容不渲染不打印（把视口移回图幅即恢复）";
                        }
                        vps.Add(item);
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                return Fail("ENUM_FAILED", ex.GetType().Name + ": " + ex.Message);
            }
            var result = new Dictionary<string, object>
            {
                { "layout", layoutName },
                { "count", vps.Count },
                { "viewports", vps }
            };
            if (sheetKnown) result["sheet"] = sheet;
            return Ok(result);
        }

        private static string GetWorkspace(string jsonIn)
        {
            var doc = ActiveDoc();
            if (doc == null) return Fail("NO_DOCUMENT", "no active document");

            string tilemode = ReadSysvarString("TILEMODE");
            string ctab = ReadSysvarString("CTAB");
            string clayer = ReadSysvarString("CLAYER");

            // 视图上下文拆分（2026-09-19）：此前单一个 view 字段在"MS-in-VP"时读到的是活动
            // 视口的模型视图（宽 1165mm），agent 误当纸面平铺视图（563mm）用——排障被带偏。
            // 现在：view = 当前生效视图（原语义不变），view_context 说明它是谁的视图。
            string cvportRaw = ReadSysvarString("CVPORT");
            int cvport;
            int.TryParse(cvportRaw, out cvport);
            string viewContext;
            if (tilemode == "1") viewContext = "model";                       // 模型选项卡
            else if (cvport == 1) viewContext = "paper";                      // 图纸空间平铺视图
            else viewContext = "viewport";                                    // 双击进了浮动视口（MS-in-VP）

            var view = new Dictionary<string, object>();
            try
            {
                var ed = doc.Editor;
                var vtr = ed.GetCurrentView();
                view["center_x"] = vtr.CenterPoint.X;
                view["center_y"] = vtr.CenterPoint.Y;
                view["height"] = vtr.Height;
                view["width"] = vtr.Width;
                var d = vtr.ViewDirection;
                view["view_direction"] = string.Format(CultureInfo.InvariantCulture,
                    "{0:F3},{1:F3},{2:F3}", d.X, d.Y, d.Z);
            }
            catch (Exception ex)
            {
                view["error"] = ex.GetType().Name;
            }
            if (viewContext == "viewport")
                view["note"] = "当前视图是活动浮动视口的模型视图（CVPORT>1）；要操纵纸面请先退回图纸空间（CVPORT=1）";

            return Ok(new Dictionary<string, object>
            {
                { "document", DocumentInfo(doc, doc, true) },
                { "tilemode", tilemode },
                { "cvport", cvport },
                { "view_context", viewContext },
                { "layout", ctab },
                { "current_layer", clayer },
                { "view", view }
            });
        }

        private static string ReadSysvarString(string name)
        {
            try { var v = Application.GetSystemVariable(name); return v == null ? null : v.ToString(); }
            catch { return null; }
        }

        private static string GetSceneSummary(string jsonIn)
        {
            var doc = ActiveDoc();
            if (doc == null) return Fail("NO_DOCUMENT", "no active document");
            var db = doc.Database;

            var byType = new Dictionary<string, long>(StringComparer.Ordinal);
            var byLayer = new Dictionary<string, long>(StringComparer.Ordinal);
            long total = 0;
            bool truncated = false;
            double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            bool hasExtents = false;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (total >= SceneSummaryEntityCap) { truncated = true; break; }
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        total++;

                        var typeName = ent.GetType().Name;
                        long n;
                        byType[typeName] = byType.TryGetValue(typeName, out n) ? n + 1 : 1;

                        long m;
                        byLayer[ent.Layer] = byLayer.TryGetValue(ent.Layer, out m) ? m + 1 : 1;

                        try
                        {
                            var e = ent.GeometricExtents;
                            if (!hasExtents)
                            {
                                minX = e.MinPoint.X; minY = e.MinPoint.Y; minZ = e.MinPoint.Z;
                                maxX = e.MaxPoint.X; maxY = e.MaxPoint.Y; maxZ = e.MaxPoint.Z;
                                hasExtents = true;
                            }
                            else
                            {
                                if (e.MinPoint.X < minX) minX = e.MinPoint.X;
                                if (e.MinPoint.Y < minY) minY = e.MinPoint.Y;
                                if (e.MinPoint.Z < minZ) minZ = e.MinPoint.Z;
                                if (e.MaxPoint.X > maxX) maxX = e.MaxPoint.X;
                                if (e.MaxPoint.Y > maxY) maxY = e.MaxPoint.Y;
                                if (e.MaxPoint.Z > maxZ) maxZ = e.MaxPoint.Z;
                            }
                        }
                        catch {  }
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                return Fail("ENUM_FAILED", ex.GetType().Name + ": " + ex.Message);
            }

            var topLayers = byLayer
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(TopLayers)
                .Select(kv => (object)new Dictionary<string, object>
                {
                    { "layer", kv.Key }, { "count", kv.Value }
                })
                .ToList();

            var extents = new Dictionary<string, object>();
            if (hasExtents)
            {
                extents["min"] = FmtPt(minX, minY, minZ);
                extents["max"] = FmtPt(maxX, maxY, maxZ);
            }

            return Ok(new Dictionary<string, object>
            {
                { "total", total },
                { "truncated", truncated },
                { "by_type", SortedCounts(byType) },
                { "top_layers", topLayers },
                { "extents", extents }
            });
        }

        private static Dictionary<string, object> SortedCounts(Dictionary<string, long> counts)
        {
            var sorted = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal);
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in sorted) result[kv.Key] = kv.Value;
            return result;
        }

        private static string FmtPt(double x, double y, double z)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2:F3}", x, y, z);
        }

        private static string UseDocument(string jsonIn)
        {
            var payload = MiniJson.ParseObject(jsonIn) ?? new Dictionary<string, object>();
            var action = payload.ContainsKey("action") ? payload["action"] as string : null;
            if (string.IsNullOrWhiteSpace(action))
                return Fail("BAD_ARGS", "action required: open | close | activate");

            var dm = Application.DocumentManager;
            action = action.Trim().ToLowerInvariant();

            if (action == "open")
            {
                var path = payload.ContainsKey("path") ? payload["path"] as string : null;
                if (string.IsNullOrWhiteSpace(path))
                    return Fail("BAD_ARGS", "open needs path");
                if (!File.Exists(path))
                    return Fail("FILE_NOT_FOUND", path);

                bool readOnly = payload.ContainsKey("read_only") && payload["read_only"] is bool && (bool)payload["read_only"];

                foreach (Document d in dm)
                {
                    string existing = null;
                    try { existing = d.Database.Filename; } catch { }
                    if (!string.IsNullOrEmpty(existing) &&
                        string.Equals(existing, path.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        dm.MdiActiveDocument = d;
                        return Ok(new Dictionary<string, object>
                        {
                            { "action", "open" }, { "already_open", true },
                            { "activated", true }, { "name", SafeName(d) }
                        });
                    }
                }

                Document opened;
                try { opened = dm.Open(path.Trim(), readOnly); }
                catch (Exception ex)
                {
                    return Fail("OPEN_FAILED", ex.GetType().Name + ": " + ex.Message);
                }
                return Ok(new Dictionary<string, object>
                {
                    { "action", "open" }, { "already_open", false },
                    { "name", SafeName(opened) },

                    { "note", "opened; P3D project mount may take 20-45s, poll list_documents if needed" }
                });
            }

            if (action == "activate")
            {
                var target = FindDocument(payload);
                if (target == null)
                    return Fail("NOT_FOUND", "no open document matches name/path");
                dm.MdiActiveDocument = target;
                return Ok(new Dictionary<string, object>
                {
                    { "action", "activate" }, { "name", SafeName(target) }, { "activated", true }
                });
            }

            if (action == "close")
            {
                object saveObj;
                bool save = payload.TryGetValue("save", out saveObj) && saveObj is bool && (bool)saveObj;
                if (!payload.ContainsKey("save"))
                    return Fail("BAD_ARGS", "close needs explicit save:true (CloseAndSave) or save:false (CloseDiscard)");

                var target = FindDocument(payload);
                if (target == null)
                    return Fail("NOT_FOUND", "no open document matches name/path");

                var active = ActiveDoc();
                if (target == active)
                    return Fail("ACTIVE_DOCUMENT",
                        "cannot close the active document from idle context — activate another document first");

                try
                {
                    if (save)
                    {

                        string fileName = null;
                        try { fileName = target.Database.Filename; } catch { }
                        DocumentExtension.CloseAndSave(target, fileName);
                    }
                    else
                    {
                        DocumentExtension.CloseAndDiscard(target);
                    }
                }
                catch (Exception ex)
                {
                    return Fail("CLOSE_FAILED", ex.GetType().Name + ": " + ex.Message
                        + " (read-only or unnamed drawings cannot CloseAndSave)");
                }
                return Ok(new Dictionary<string, object>
                {
                    { "action", "close" }, { "name", SafeName(target) }, { "saved", save }
                });
            }

            return Fail("BAD_ARGS", "unknown action: " + action + " (open | close | activate)");
        }

        private static Document FindDocument(Dictionary<string, object> payload)
        {
            var name = payload.ContainsKey("name") ? payload["name"] as string : null;
            var path = payload.ContainsKey("path") ? payload["path"] as string : null;

            if (!string.IsNullOrWhiteSpace(path))
            {
                // 容错匹配（2026-09-19）：中文路径经 MCP→JSON→宿主多次转手后曾出现
                // 全等比较 NOT_FOUND（名称子串反而命中）。匹配梯度：全等 → GetFullPath
                // 规范化全等 → 文件名 EndsWith（含目录后缀优先）。
                var needle = path.Trim();
                string needleFull = null;
                try { needleFull = Path.GetFullPath(needle); } catch { }
                var needleFile = Path.GetFileName(needle);
                Document fallback = null;
                foreach (Document d in Application.DocumentManager)
                {
                    string f = null;
                    try { f = d.Database.Filename; } catch { }
                    if (string.IsNullOrEmpty(f)) continue;
                    if (string.Equals(f, needle, StringComparison.OrdinalIgnoreCase)) return d;
                    if (needleFull != null)
                    {
                        string fFull = null;
                        try { fFull = Path.GetFullPath(f); } catch { }
                        if (fFull != null && string.Equals(fFull, needleFull, StringComparison.OrdinalIgnoreCase)) return d;
                    }
                    if (fallback == null && !string.IsNullOrEmpty(needleFile)
                        && f.EndsWith(needleFile, StringComparison.OrdinalIgnoreCase))
                        fallback = d;
                }
                return fallback;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                var needle = name.Trim();
                Document byName = null;
                foreach (Document d in Application.DocumentManager)
                {
                    if (SafeName(d).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        return d;
                    string f = null;
                    try { f = d.Database.Filename; } catch { }
                    if (byName == null && !string.IsNullOrEmpty(f) &&
                        f.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        byName = d;
                }
                return byName;
            }
            return null;
        }

        private static string Ok(Dictionary<string, object> data)
        {
            data["ok"] = true;
            return MiniJson.Write(data);
        }

        private static string Fail(string code, string message)
        {
            return MiniJson.Write(new Dictionary<string, object>
            {
                { "ok", false }, { "code", code }, { "message", message }
            });
        }
    }
}

