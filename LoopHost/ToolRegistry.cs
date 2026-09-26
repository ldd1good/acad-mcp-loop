using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace LoopHost
{

    public sealed class ToolRegistry
    {
        private static readonly ToolRegistry _instance = new ToolRegistry();
        public static ToolRegistry Instance { get { return _instance; } }

        private readonly ConcurrentDictionary<string, Func<string, string>> _businessTools
            = new ConcurrentDictionary<string, Func<string, string>>(StringComparer.Ordinal);

        private readonly object _teardownLock = new object();
        private Action _teardown;
        private int _epoch;

        private ToolRegistry() { }

        public int Epoch { get { return Volatile.Read(ref _epoch); } }

        public void ResetBusinessTools()
        {
            _businessTools.Clear();
        }

        public void Register(string name, Func<string, string> handler)
        {
            if (string.IsNullOrEmpty(name) || handler == null) return;
            _businessTools[name] = handler;
            RingLogSink.Log("registry", "tool registered: " + name);
        }

        public bool TryGet(string name, out Func<string, string> handler)
        {
            return _businessTools.TryGetValue(name ?? string.Empty, out handler);
        }

        public List<string> Names()
        {
            return new List<string>(_businessTools.Keys);
        }

        public void SetTeardown(Action teardown)
        {
            lock (_teardownLock) { _teardown = teardown; }
        }

        public bool RunTeardownSafely()
        {
            Action teardown;
            lock (_teardownLock)
            {
                teardown = _teardown;
                _teardown = null;
            }
            if (teardown == null) return true;
            try
            {
                teardown();
                RingLogSink.Log("registry", "teardown ok");
                return true;
            }
            catch (Exception ex)
            {
                RingLogSink.Log("registry", "teardown failed (ignored): " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        public int AdvanceEpoch()
        {
            return Interlocked.Increment(ref _epoch);
        }
    }
}

