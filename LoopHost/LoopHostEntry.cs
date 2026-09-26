using System;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace LoopHost
{

    public sealed class LoopHostEntry : IExtensionApplication
    {
        public void Initialize()
        {
            EventHandler onIdle = null;
            onIdle = (sender, args) =>
            {
                Application.Idle -= onIdle;
                try { HostRuntime.Start(); }
                catch (System.Exception) {  }
            };
            Application.Idle += onIdle;
        }

        public void Terminate() { }

        [CommandMethod("LOOPHOST")]
        public static void StartHost()
        {
            HostRuntime.Start();
        }
    }

    public sealed class HostRuntime
    {
        private static int _started;
        private static DateTime _startedUtc;

        public static DateTime StartedUtc { get { return _startedUtc; } }

        public static bool IsStarted { get { return System.Threading.Volatile.Read(ref _started) == 1; } }

        public static void Start()
        {
            if (System.Threading.Interlocked.Exchange(ref _started, 1) == 1)
            {
                WriteToEditor("\n[LoopHost] already running (epoch=" + ToolRegistry.Instance.Epoch + ")");
                return;
            }

            try
            {
                _startedUtc = DateTime.UtcNow;
                MainThreadPump.Instance.Start();
                PipeServer.Instance.Start();
                RegisterExistingBusinessIfLoaded();

                WriteToEditor("\n[LoopHost] started on pipe '" + PipeServer.PipeName
                    + "', epoch=" + ToolRegistry.Instance.Epoch
                    + ", tools=" + ToolRegistry.Instance.Names().Count);
            }
            catch (System.Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _started, 0);
                RingLogSink.Log("runtime", "start failed: " + ex.Message);
                WriteToEditor("\n[LoopHost] start FAILED: " + ex.Message);
                throw;
            }
        }

        private static void RegisterExistingBusinessIfLoaded()
        {
            try
            {
                var regType = FindRegistrationType();
                if (regType == null) return;

                ReloadOrchestrator.InvokeBusinessRegister(regType);
                int epoch = ToolRegistry.Instance.AdvanceEpoch();
                RingLogSink.Log("runtime", "existing business gen registered, epoch=" + epoch
                    + " version=" + ReloadOrchestrator.Instance.LoadedBusinessVersion);
            }
            catch (System.Exception ex)
            {
                RingLogSink.Log("runtime", "existing gen registration failed: " + ex.Message);
            }
        }

        private static Type FindRegistrationType()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != ReloadOrchestrator.BusinessAssemblyName) continue;
                try
                {
                    var t = a.GetType(ReloadOrchestrator.RegistrationTypeFullName, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static void WriteToEditor(string message)
        {
            try
            {
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                if (ed != null) ed.WriteMessage(message);
            }
            catch { }
        }
    }
}

