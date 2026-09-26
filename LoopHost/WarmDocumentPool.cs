using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace LoopHost
{

    public sealed class WarmDocumentPool
    {
        private static readonly WarmDocumentPool _instance = new WarmDocumentPool();
        public static WarmDocumentPool Instance { get { return _instance; } }

        private WarmDocumentPool() { }

        private static string DebugRoot
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("LOOP_DEBUG_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return env;
                var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir, "build.ps1")))
                        return Path.Combine(dir, ".debug");
                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                }
                return Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    ".debug");
            }
        }

        public string PrepareCopy(string fixturePath)
        {
            if (string.IsNullOrEmpty(fixturePath) || !File.Exists(fixturePath))
                throw new FileNotFoundException("fixture not found", fixturePath);

            string dir = Path.Combine(DebugRoot, "runs", "warm");
            Directory.CreateDirectory(dir);

            string name = Path.GetFileNameWithoutExtension(fixturePath);
            string copyPath = Path.Combine(dir,
                name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + Path.GetExtension(fixturePath));
            File.Copy(fixturePath, copyPath, overwrite: true);
            RingLogSink.Log("warm", "fixture copy ready: " + copyPath);
            return copyPath;
        }

        public object UndoMark()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("no active document");
            doc.SendStringToExecute("_.UNDO _.MARK ", true, false, false);
            RingLogSink.Log("warm", "undo mark queued");
            return new Dictionary<string, object> { { "queued", true } };
        }

        public object UndoBack()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("no active document");
            doc.SendStringToExecute("_.UNDO _.BACK ", true, false, false);
            RingLogSink.Log("warm", "undo back queued");
            return new Dictionary<string, object> { { "queued", true } };
        }

        public string BaselineSignature()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) throw new InvalidOperationException("no active document");
            var db = doc.Database;

            var lines = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string ext = "-";
                    try
                    {
                        var e = ent.GeometricExtents;

                        ext = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0:F3},{1:F3},{2:F3}|{3:F3},{4:F3},{5:F3}",
                            e.MinPoint.X, e.MinPoint.Y, e.MinPoint.Z,
                            e.MaxPoint.X, e.MaxPoint.Y, e.MaxPoint.Z);
                    }
                    catch { }
                    lines.Add(ent.Id.Handle.ToString() + "|" + ent.GetType().Name + "|" + ent.Layer + "|" + ext);
                }
                tr.Commit();
            }

            lines.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public object Status()
        {
            string warmDir = Path.Combine(DebugRoot, "runs", "warm");
            int copies = 0;
            try { copies = Directory.GetFiles(warmDir, "*.dwg").Length; }
            catch { }
            return new Dictionary<string, object>
            {
                { "warm_copies", copies },
                { "v0", true },
                { "note", "S3 will add signature gate + sysvar reset + L2/L3" }
            };
        }
    }
}

