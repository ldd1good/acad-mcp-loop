using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace LoopHost
{

    public class AssemblyDependent
    {
        string _dllFile;

        private static readonly HashSet<string> _cadStartupFullNames =
            CaptureStartupAssemblies();

        private static HashSet<string> CaptureStartupAssemblies()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                set.Add(a.FullName);
            return set;
        }

        private static int _resolveHandlerRegistered = 0;

        public AssemblyDependent(string dllFile)
        {
            try
            {
                _dllFile = dllFile;
                RingLogSink.Log("ad", "AssemblyDependent init, target: " + dllFile);

                if (Interlocked.CompareExchange(ref _resolveHandlerRegistered, 1, 0) == 0)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += RunTimeCurrentDomain.DefaultAssemblyResolve;
                    RingLogSink.Log("ad", "default assembly resolve handler registered (once)");
                }
                else
                {
                    RingLogSink.Log("ad", "resolve handler already registered, skip");
                }
            }
            catch (Exception ex)
            {
                RingLogSink.Log("ad", "AssemblyDependent ctor failed: " + ex.Message);
                throw;
            }
        }

        public class LoadDllMessage
        {
            public string Path;
            public bool LoadYes;

            public bool Skipped;

            public string Reason;

            public LoadDllMessage(string path, bool loadYes, bool skipped = false, string reason = null)
            {
                Path = path;
                LoadYes = loadYes;
                Skipped = skipped;
                Reason = reason;
            }
        }

        public LoadDllMessage[] Load()
        {
            var loadYesList = new List<LoadDllMessage>();

            try
            {
                RingLogSink.Log("ad", "byte load start: " + _dllFile);

                if (!File.Exists(_dllFile))
                {
                    RingLogSink.Log("ad", "file not exists: " + _dllFile);
                    return loadYesList.ToArray();
                }

                var allRefs = GetAllRefPaths(_dllFile);
                allRefs.Reverse();

                RingLogSink.Log("ad", "dependency closure: " + allRefs.Count + " file(s)");

                foreach (var path in allRefs)
                {
                    try
                    {

                        string assName = AssemblyName.GetAssemblyName(path).FullName;

                        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                            .FirstOrDefault((Assembly a) => a.FullName == assName);

                        if (assembly != null)
                        {

                            bool isTarget = string.Equals(path, _dllFile, StringComparison.OrdinalIgnoreCase);

                            bool isSystemPath = IsAutodeskInstallPath(path);

                            if (!isTarget && (_cadStartupFullNames.Contains(assName) || isSystemPath))
                            {

                                string why = isSystemPath && !_cadStartupFullNames.Contains(assName)
                                    ? "system_path" : "system_snapshot";
                                RingLogSink.Log("ad", "skip (cad builtin, " + why + "): " + assName);
                                loadYesList.Add(new LoadDllMessage(path, false, skipped: true, reason: why));
                            }
                            else
                            {

                                string why = isTarget ? "target_analysis_loaded" : "same_identity_in_memory";
                                RingLogSink.Log("ad", "skip (" + why + "): " + assName);
                                loadYesList.Add(new LoadDllMessage(path, false, skipped: true, reason: why));
                            }
                        }
                        else
                        {

                            ReloadFromDisk(path);
                            RingLogSink.Log("ad", "loaded: " + path + " (" + assName + ")");
                            loadYesList.Add(new LoadDllMessage(path, true));
                        }
                    }
                    catch (Exception ex)
                    {
                        RingLogSink.Log("ad", "load single dll failed: " + path + " - " + ex.Message);
                        loadYesList.Add(new LoadDllMessage(path, false, reason: "load_failed"));
                    }
                }

                RingLogSink.Log("ad", "byte load done, files=" + allRefs.Count
                    + ", cad startup snapshot=" + _cadStartupFullNames.Count);
                return loadYesList.ToArray();
            }
            catch (Exception ex)
            {
                RingLogSink.Log("ad", "byte load failed: " + ex.Message);
                return loadYesList.ToArray();
            }
        }

        private static bool IsAutodeskInstallPath(string path)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                foreach (var root in new[] { pf, pf86 })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    var cadRoot = System.IO.Path.Combine(root, "Autodesk");
                    if (full.StartsWith(cadRoot, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void ReloadFromDisk(string path)
        {
            var buffer = File.ReadAllBytes(path);
#if DEBUG
            var dir = Path.GetDirectoryName(path);
            var pdbName = Path.GetFileNameWithoutExtension(path) + ".pdb";
            var pdbFullName = Path.Combine(dir, pdbName);
            if (File.Exists(pdbFullName))
            {
                var pdbbuffer = File.ReadAllBytes(pdbFullName);
                Assembly.Load(buffer, pdbbuffer);
            }
            else
            {
                Assembly.Load(buffer);
            }
#else
            Assembly.Load(buffer);
#endif
        }

        List<string> GetAllRefPaths(string dll, List<string> dlls = null)
        {
            dlls = dlls ?? new List<string>();

            if (dlls.Contains(dll) || !File.Exists(dll))
            {
                return dlls;
            }
            dlls.Add(dll);

            string assName = AssemblyName.GetAssemblyName(dll).FullName;

            Assembly assemblyToAnalyze = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault((Assembly a) => a.FullName == assName);

            if (assemblyToAnalyze == null)
            {
                try
                {

                    var buffer = File.ReadAllBytes(dll);
#if DEBUG
                    var dir = Path.GetDirectoryName(dll);
                    var pdbName = Path.GetFileNameWithoutExtension(dll) + ".pdb";
                    var pdbFullName = Path.Combine(dir, pdbName);
                    if (File.Exists(pdbFullName))
                    {
                        var pdbbuffer = File.ReadAllBytes(pdbFullName);
                        assemblyToAnalyze = Assembly.Load(buffer, pdbbuffer);
                    }
                    else
                    {
                        assemblyToAnalyze = Assembly.Load(buffer);
                    }
#else
                    assemblyToAnalyze = Assembly.Load(buffer);
#endif
                }
                catch (Exception ex)
                {
                    RingLogSink.Log("ad", "dependency analysis load failed: " + dll + " - " + ex.Message);
                    return dlls;
                }
            }

            foreach (var assemblyName in assemblyToAnalyze.GetReferencedAssemblies())
            {

                string directoryName = Path.GetDirectoryName(dll);

                var path = directoryName + "\\" + assemblyName.Name;
                var paths = new string[]
                {
                    path + ".dll",
                    path + ".exe"
                };
                foreach (var patha in paths)
                {
                    GetAllRefPaths(patha, dlls);
                }
            }
            return dlls;
        }
    }

    public static class RunTimeCurrentDomain
    {

        public static Assembly DefaultAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var cad = AppDomain.CurrentDomain.GetAssemblies();

            Assembly load = null;
            load = cad.FirstOrDefault(a => a.GetName().FullName == args.Name);
            if (load == null)
            {

                var ag = args.Name.Split(',')[0];
                foreach (var item in cad)
                {
                    if (item.GetName().FullName.Split(',')[0] == ag)
                    {
                        load = item;
                    }
                }
            }

            return load;
        }
    }
}

