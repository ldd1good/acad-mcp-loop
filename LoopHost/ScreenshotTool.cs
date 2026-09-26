using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace LoopHost
{
    /// <summary>
    /// MCP 基础能力：当前视图出图（agent 闭环取图的通用感官，业务无关）。
    /// Document.CapturePreviewImage 一步抓位图，不走 PLOT 管线
    /// （验证账本 reports/ortho-v2-hires-notes.md 决议 2026-09-09：
    /// 高度如实执行且可远超窗口分辨率；宽度被重算为 高×~1.4126 锁定纵横比
    /// ——要 N 宽就请求高 ceil(N/1.4126)，多余高度是图纸空白边，可中心裁剪）。
    /// 仅限布局上下文（模型空间未验证）。宿主级工具：任何业务项目零移植可用。
    /// 不改任何 CAD 状态：不缩放（宿主 Idle 上下文做不到，见 Handle 注释），改为按视口框
    /// 做像素裁剪 frame_path——要更大的框内图就加大 width（裁剪按比例放大）。
    /// </summary>
    public static class ScreenshotTool
    {
        private const double AspectLock = 1.4126;
        private const int SettleMs = 800;
        private const int MinWidth = 640;
        private const int MaxWidth = 7680;
        private const int FrameMarginPx = 6;
        // 实体裁剪边距放大：探针实体常只有几 mm（2.5mm 字高），紧贴边线取证效果差
        private const int EntityMarginPx = 60;
        private const int MinFramePx = 64;
        // 默认目录：LoopHost.dll 旁 loop.config.json 的 shots_dir，缺省向上找仓库根（build.ps1）
        // 的 reports\agent-shots，找不到根则落 dll 旁 agent-shots（NETLOAD 位置不定，不硬编码绝对路径）
        private static string DefaultDir
        {
            get
            {
                var cfg = HostConfig.Get("shots_dir", null);
                if (!string.IsNullOrWhiteSpace(cfg)) return cfg;
                var dllDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                var dir = dllDir;
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir, "build.ps1")))
                        return Path.Combine(dir, "reports", "agent-shots");
                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                }
                return Path.Combine(dllDir ?? ".", "agent-shots");
            }
        }

        /// <summary>payload: width(默认3840) / crop_w / crop_h / crop_entity_prefix /
        /// crop_mm{x,y,w,h} / file(基名，默认 shot) / dir(默认仓库 agent-shots)。</summary>
        public static string Handle(string jsonIn)
        {
            var payload = MiniJson.ParseObject(jsonIn) ?? new Dictionary<string, object>();
            int width = Clamp(GetInt(payload, "width", 3840), MinWidth, MaxWidth);
            int? cropW = GetIntOpt(payload, "crop_w");
            int? cropH = GetIntOpt(payload, "crop_h");
            string entPrefix = GetString(payload, "crop_entity_prefix", null);
            Dictionary<string, object> cropMm = GetDict(payload, "crop_mm");
            string file = SafeBaseName(GetString(payload, "file", "shot"));
            string dir = GetString(payload, "dir", null);
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultDir;

            Document doc = null;
            try { doc = Application.DocumentManager.MdiActiveDocument; } catch { }
            if (doc == null) return Fail("NO_DOCUMENT", "no active document");
            var db = doc.Database;

            var vpBox = ViewportBox(db);
            var box = vpBox ?? PaperBox(db);
            if (box == null) return Fail("NO_EXTENTS", "no viewport bounds / paper extents");

            // 缩放会把实时视图写回浮动视口实体（“激活态缩放事故”：ViewCenter/ViewTarget/
            // CustomScale/TwistAngle 被纸面框覆盖 → 投影退化为恒等），故取图前先备份该视口的
            // 模型视图，取完原样写回（无论走哪条缩放路径都写回，失败在 view_restore_error 报出）。
            var viewBackup = ReadViewportView(db);
            var data = new Dictionary<string, object>();

            // P3-2：按实体包围盒 / 纸面 mm 矩形裁剪的目标框（当前视图坐标，取图后经 ToPixels 映射）
            Extents3d? entBox = null;
            if (!string.IsNullOrEmpty(entPrefix))
            {
                int matched, scanned;
                var raw = EntityBoxByXDataPrefix(db, entPrefix, out matched, out scanned);
                data["ent_scanned"] = scanned;
                data["ent_count"] = matched;
                if (raw != null)
                {
                    // 实体本体常只有几 mm（2.5mm 探针字），紧贴裁出来不可读：
                    // 纸面坐标外扩 pad_mm 取上下文窗（px 边距随分辨率变化，mm 恒定）
                    double pad = GetDouble(payload, "crop_entity_pad_mm");
                    if (pad <= 0) pad = 30;
                    entBox = new Extents3d(
                        new Point3d(raw.Value.MinPoint.X - pad, raw.Value.MinPoint.Y - pad, 0),
                        new Point3d(raw.Value.MaxPoint.X + pad, raw.Value.MaxPoint.Y + pad, 0));
                }
            }
            Extents3d? mmBox = null;
            if (cropMm != null)
            {
                double mx = GetDouble(cropMm, "x"), my = GetDouble(cropMm, "y");
                double mw = GetDouble(cropMm, "w"), mh = GetDouble(cropMm, "h");
                if (mw > 0 && mh > 0)
                    mmBox = new Extents3d(new Point3d(mx, my, 0), new Point3d(mx + mw, my + mh, 0));
                else data["mm_note"] = "crop_mm requires positive w/h";
            }

            string failCode = null, failMsg = null;
            // 不缩放：宿主的 Idle 上下文不能执行命令、也不能设系统变量（ZOOM / VIEWSIZE / VIEWCTR
            // 均抛 eInvalidInput，2026-09-10 实测）；而 SetCurrentView 会把纸面值写进浮动视口的
            // 模型视图——视口激活态下必毁图（模型内容被缩没）。故改为纯像素运算：
            // 当前视图（图纸单位）→ 像素的线性映射，把视口框裁出来。零 CAD 状态改动。
            var snap = ViewSnapshot.Capture(doc.Editor);
            int cvport = -1;
            try { cvport = Convert.ToInt32(Application.GetSystemVariable("CVPORT")); } catch { }
            string framedPath = null;
            try
            {
                data["zoom"] = "none";
                data["cvport"] = cvport;
                if (snap == null) data["frame_note"] = "current view unreadable; no frame crop";
                else if (cvport != 1)
                    data["frame_note"] = "floating viewport active (CVPORT=" + cvport
                        + "); GetCurrentView returns the model view, paper-space frame crop needs CVPORT=1";
                Thread.Sleep(SettleMs); // 等视图重生成稳定

                int reqH = (int)Math.Ceiling(width / AspectLock);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string full = Path.Combine(dir, file + ".png");
                using (var bmp = doc.CapturePreviewImage((uint)width, (uint)reqH))
                {
                    if (bmp == null)
                    {
                        failCode = "NULL_CAPTURE";
                        failMsg = "CapturePreviewImage returned null (window minimized?)";
                    }
                    else
                    {
                        data["w"] = bmp.Width;
                        data["h"] = bmp.Height;
                        bmp.Save(full, System.Drawing.Imaging.ImageFormat.Png);
                        data["path"] = full;
                        if (snap != null && cvport == 1)
                        {
                            var rc = snap.Value.ToPixels(bmp.Width, bmp.Height, box.Value, FrameMarginPx, out bool clipped);
                            bool whole = rc.Width >= bmp.Width - 2 && rc.Height >= bmp.Height - 2;
                            if (rc.Width >= MinFramePx && rc.Height >= MinFramePx && !whole)
                            {
                                framedPath = Path.Combine(dir, file + "-vp.png");
                                using (var frame = bmp.Clone(rc, bmp.PixelFormat))
                                    frame.Save(framedPath, System.Drawing.Imaging.ImageFormat.Png);
                                data["frame_path"] = framedPath;
                                data["frame_px"] = string.Format("x={0} y={1} w={2} h={3}",
                                    rc.X, rc.Y, rc.Width, rc.Height);
                                if (clipped)
                                    data["frame_note"] = "viewport box partly outside current view; "
                                        + "frame covers the visible part only (zoom/pan the layout to see it whole)";
                            }
                            else
                            {
                                data["frame_note"] = whole
                                    ? "viewport box covers whole view; no crop"
                                    : "viewport box too small in this view; no crop (raise width)";
                            }
                        }
                        // P3-2：按 xdata 前缀实体包围盒裁剪（探针取证：小实体一步定位，
                        // 免去 frame_px 换算试错）。与视口框裁剪同约束：纸面态（CVPORT=1）。
                        if (entBox != null && snap != null)
                        {
                            if (cvport != 1)
                                data["ent_note"] = "entity crop needs CVPORT=1 (paper-space pixel mapping)";
                            else if ((int)data["ent_count"] > 0)
                            {
                                var rc = snap.Value.ToPixels(bmp.Width, bmp.Height, entBox.Value,
                                    EntityMarginPx, out bool entClipped);
                                if (rc.Width >= MinFramePx && rc.Height >= MinFramePx)
                                {
                                    string entPath = Path.Combine(dir, file + "-ent.png");
                                    using (var entImg = bmp.Clone(rc, bmp.PixelFormat))
                                        entImg.Save(entPath, System.Drawing.Imaging.ImageFormat.Png);
                                    data["ent_path"] = entPath;
                                    data["ent_px"] = string.Format("x={0} y={1} w={2} h={3}",
                                        rc.X, rc.Y, rc.Width, rc.Height);
                                    if (entClipped)
                                        data["ent_note"] = "entity box partly outside current view";
                                }
                                else if (entClipped)
                                    data["ent_note"] = "entity box outside current view — "
                                        + "zoom/pan the layout to include it, then retry";
                                else data["ent_note"] = "entity box too small in this view (raise width)";
                            }
                            else data["ent_note"] = "no entity xdata 1000 value contains prefix '" + entPrefix + "'";
                        }
                        // P3-2：按纸面坐标矩形裁剪 + 透出 mm↔像素换算率（任意坐标自助换算）
                        if (mmBox != null && snap != null)
                        {
                            var rc = snap.Value.ToPixels(bmp.Width, bmp.Height, mmBox.Value,
                                FrameMarginPx, out bool mmClipped);
                            if (rc.Width >= MinFramePx && rc.Height >= MinFramePx)
                            {
                                string mmPath = Path.Combine(dir, file + "-mm.png");
                                using (var mmImg = bmp.Clone(rc, bmp.PixelFormat))
                                    mmImg.Save(mmPath, System.Drawing.Imaging.ImageFormat.Png);
                                data["mm_path"] = mmPath;
                                data["mm_px"] = string.Format("x={0} y={1} w={2} h={3}",
                                    rc.X, rc.Y, rc.Width, rc.Height);
                                if (mmClipped)
                                    data["mm_note"] = "mm box partly outside current view";
                            }
                            else data["mm_note"] = "mm box too small in this view (raise width)";
                        }
                        if (snap != null)
                        {
                            data["mm_per_px_x"] = snap.Value.W / bmp.Width;
                            data["mm_per_px_y"] = snap.Value.H / bmp.Height;
                        }
                        if (cropW.HasValue && cropH.HasValue
                            && bmp.Width >= cropW.Value && bmp.Height >= cropH.Value)
                        {
                            string cropPath = Path.Combine(dir, file + "-crop.png");
                            using (var crop = bmp.Clone(new System.Drawing.Rectangle(
                                (bmp.Width - cropW.Value) / 2, (bmp.Height - cropH.Value) / 2,
                                cropW.Value, cropH.Value), bmp.PixelFormat))
                                crop.Save(cropPath, System.Drawing.Imaging.ImageFormat.Png);
                            data["crop_path"] = cropPath;
                            data["crop_w"] = cropW.Value;
                            data["crop_h"] = cropH.Value;
                        }
                        string src = framedPath ?? full;
                        string latest = Path.Combine(dir, "latest.png");
                        File.Copy(src, latest, true);
                        data["latest"] = latest;
                        data["latest_from"] = framedPath != null ? "frame" : "full";
                    }
                }
            }
            catch (Exception ex)
            {
                failCode = "CAPTURE_FAILED";
                failMsg = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (viewBackup != null)
                {
                    string restoreErr;
                    data["view_restored"] = RestoreViewportView(db, viewBackup.Value, out restoreErr);
                    if (restoreErr != null) data["view_restore_error"] = restoreErr;
                }
            }
            if (failCode != null) return Fail(failCode, failMsg);
            data["ok"] = true;
            return MiniJson.Write(data);
        }

        /// <summary>当前视图快照（图纸单位）。不缩放取图靠它把目标框换算成图像像素矩形。</summary>
        private struct ViewSnapshot
        {
            public double Cx, Cy, W, H;

            public static ViewSnapshot? Capture(Editor ed)
            {
                try
                {
                    var v = ed.GetCurrentView();
                    if (v.Width <= 0 || v.Height <= 0) return null;
                    return new ViewSnapshot
                    {
                        Cx = v.CenterPoint.X,
                        Cy = v.CenterPoint.Y,
                        W = v.Width,
                        H = v.Height
                    };
                }
                catch { return null; }
            }

            /// <summary>图纸单位矩形 → 图像像素矩形（图像上边缘 = 视图上边缘，y 轴向下）。
            /// clipped=true 表示矩形超出当前视图，返回值已钳位到图像范围内。</summary>
            public System.Drawing.Rectangle ToPixels(int pxW, int pxH, Extents3d box, int margin, out bool clipped)
            {
                double sx = pxW / W, sy = pxH / H;
                double left = box.MinPoint.X - (Cx - W / 2);
                double right = box.MaxPoint.X - (Cx - W / 2);
                double top = (Cy + H / 2) - box.MaxPoint.Y;
                double bottom = (Cy + H / 2) - box.MinPoint.Y;
                double fx0 = Math.Floor(left * sx), fy0 = Math.Floor(top * sy);
                double fx1 = Math.Ceiling(right * sx), fy1 = Math.Ceiling(bottom * sy);
                clipped = fx0 < 0 || fy0 < 0 || fx1 > pxW || fy1 > pxH;
                int x0 = (int)fx0 - margin, y0 = (int)fy0 - margin;
                int x1 = (int)fx1 + margin, y1 = (int)fy1 + margin;
                if (x0 < 0) x0 = 0;
                if (y0 < 0) y0 = 0;
                if (x1 > pxW) x1 = pxW;
                if (y1 > pxH) y1 = pxH;
                return new System.Drawing.Rectangle(x0, y0,
                    Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
            }
        }

        /// <summary>取图视口边框：优先当前激活浮动视口，否则纸面第一个开启的浮动视口——真正的图面区域。</summary>
        private static Extents3d? ViewportBox(Database db)
        {
            try
            {
                Extents3d? box = null;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId vpId = FindFloatingViewportId(db, tr);
                    if (!vpId.IsNull)
                    {
                        var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                        try { box = vp.Bounds; } catch { }
                    }
                    tr.Commit();
                }
                return box;
            }
            catch { return null; }
        }

        /// <summary>优先 CVPORT 指向的浮动视口（On 且 Number&gt;1），否则纸面第一个开启的浮动视口；无则 Null。</summary>
        private static ObjectId FindFloatingViewportId(Database db, Transaction tr)
        {
            int cvport = 0;
            try { cvport = Convert.ToInt32(Application.GetSystemVariable("CVPORT")); } catch { }
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(BlockTableRecord.PaperSpace)) return ObjectId.Null;
            var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.PaperSpace], OpenMode.ForRead);
            ObjectId first = ObjectId.Null;
            foreach (ObjectId id in btr)
            {
                var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                if (vp == null || !vp.On || vp.Number <= 1) continue;
                if (cvport > 1 && vp.Number == cvport) return id;
                if (first.IsNull) first = id;
            }
            return first;
        }

        /// <summary>浮动视口模型视图四元组（激活态缩放事故的备份）。</summary>
        private struct ViewportViewBackup
        {
            public ObjectId ViewportId;
            public Point2d ViewCenter;
            public Point3d ViewTarget;
            public double CustomScale;
            public double TwistAngle;
        }

        /// <summary>读浮动视口的模型视图；无浮动视口或读取失败返回 null。</summary>
        private static ViewportViewBackup? ReadViewportView(Database db)
        {
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId vpId = FindFloatingViewportId(db, tr);
                    if (vpId.IsNull) { tr.Commit(); return null; }
                    var vp = (Viewport)tr.GetObject(vpId, OpenMode.ForRead);
                    var b = new ViewportViewBackup
                    {
                        ViewportId = vpId,
                        ViewCenter = vp.ViewCenter,
                        ViewTarget = vp.ViewTarget,
                        CustomScale = vp.CustomScale,
                        TwistAngle = vp.TwistAngle
                    };
                    tr.Commit();
                    return b;
                }
            }
            catch { return null; }
        }

        /// <summary>把备份的模型视图原样写回视口（缩放事故自愈）；返回是否写回成功。</summary>
        private static bool RestoreViewportView(Database db, ViewportViewBackup b, out string error)
        {
            error = null;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var vp = tr.GetObject(b.ViewportId, OpenMode.ForWrite) as Viewport;
                    if (vp == null) { tr.Commit(); error = "viewport gone"; return false; }
                    // 值未变则不写：避免只读式备份把图纸标脏（纸面空间路径根本没碰视口实体）
                    if (vp.ViewCenter == b.ViewCenter && vp.ViewTarget == b.ViewTarget
                        && vp.CustomScale == b.CustomScale && vp.TwistAngle == b.TwistAngle)
                    { tr.Commit(); return true; }
                    vp.ViewCenter = b.ViewCenter;
                    vp.ViewTarget = b.ViewTarget;
                    vp.CustomScale = b.CustomScale;
                    vp.TwistAngle = b.TwistAngle;
                    tr.Commit();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// P3-2：按 xdata 前缀找实体包围盒并集。匹配规则与业务侧 CleanProbeLeaders 一致：
        /// 实体任一 RegApp 的 1000 型字符串值含前缀（探针 MLeader 带 "mlprobe:" 前缀）。
        /// 纸面态（CVPORT=1）扫纸面空间，否则扫当前空间——与探针落位一致。
        /// </summary>
        private static Extents3d? EntityBoxByXDataPrefix(Database db, string prefix, out int matched, out int scanned)
        {
            matched = 0;
            scanned = 0;
            Extents3d? ext = null;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    int cvport = 0;
                    try { cvport = Convert.ToInt32(Application.GetSystemVariable("CVPORT")); } catch { }
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId spaceId = cvport == 1 && bt.Has(BlockTableRecord.PaperSpace)
                        ? bt[BlockTableRecord.PaperSpace]
                        : db.CurrentSpaceId;
                    var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        scanned++;
                        Entity ent;
                        try { ent = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (ent == null) continue;
                        try
                        {
                            var xdata = ent.XData;
                            if (xdata == null) continue;
                            bool hit = false;
                            foreach (var tv in xdata)
                            {
                                if (tv.TypeCode == 1000 && tv.Value is string s
                                    && s.IndexOf(prefix, StringComparison.Ordinal) >= 0)
                                {
                                    hit = true;
                                    break;
                                }
                            }
                            if (!hit) continue;
                            var e = ent.GeometricExtents;
                            ext = ext == null ? e : Union(ext.Value, e);
                            matched++;
                        }
                        catch { }
                    }
                    tr.Commit();
                }
            }
            catch { }
            return ext;
        }

        /// <summary>纸面全部实体的包围盒（无激活视口时兜底，可能含杂散实体）。</summary>
        private static Extents3d? PaperBox(Database db)        {
            try
            {
                Extents3d? ext = null;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(BlockTableRecord.PaperSpace))
                    {
                        tr.Commit();
                        return null;
                    }
                    var btr = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.PaperSpace], OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        try
                        {
                            if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                            var e = ent.GeometricExtents;
                            ext = ext == null ? e : Union(ext.Value, e);
                        }
                        catch { }
                    }
                    tr.Commit();
                }
                return ext;
            }
            catch { return null; }
        }

        private static Extents3d Union(Extents3d a, Extents3d b)
        {
            return new Extents3d(
                new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y), 0),
                new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), 0));
        }

        private static string SafeBaseName(string raw)
        {
            var name = (raw ?? "shot").Trim();
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return "shot";
            return name.Length == 0 ? "shot" : name;
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        private static int GetInt(Dictionary<string, object> p, string key, int def)
        {
            object v;
            if (!p.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt32(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
            catch { return def; }
        }

        private static int? GetIntOpt(Dictionary<string, object> p, string key)
        {
            object v;
            if (!p.TryGetValue(key, out v) || v == null) return null;
            try { return Convert.ToInt32(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
            catch { return null; }
        }

        private static string GetString(Dictionary<string, object> p, string key, string def)
        {
            object v;
            if (!p.TryGetValue(key, out v) || v == null) return def;
            return v as string ?? def;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> p, string key)
        {
            object v;
            if (!p.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        private static double GetDouble(Dictionary<string, object> p, string key)
        {
            object v;
            if (!p.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
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
