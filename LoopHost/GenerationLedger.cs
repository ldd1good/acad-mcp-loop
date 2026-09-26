using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LoopHost
{

    public static class GenerationLedger
    {
        private sealed class Slot
        {
            public string BuildGuid;
            public int ClaimedAtEpoch;
        }

        private static readonly ConcurrentDictionary<string, Slot> _slots =
            new ConcurrentDictionary<string, Slot>(StringComparer.Ordinal);

        public static void Claim(string key, string buildGuid)
        {
            if (string.IsNullOrEmpty(key)) return;
            _slots[key] = new Slot { BuildGuid = buildGuid ?? "?", ClaimedAtEpoch = ToolRegistry.Instance.Epoch };
        }

        public static void Release(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            Slot _;
            _slots.TryRemove(key, out _);
        }

        public static List<Dictionary<string, object>> LeakSnapshot(string expectedGuid, bool teardownThrew)
        {
            var leaks = new List<Dictionary<string, object>>();
            int currentEpoch = ToolRegistry.Instance.Epoch;
            foreach (var kv in _slots)
            {
                if (string.Equals(kv.Value.BuildGuid, expectedGuid, StringComparison.Ordinal)) continue;
                var item = new Dictionary<string, object>
                {
                    { "key", kv.Key },
                    { "owner_guid", kv.Value.BuildGuid },
                    { "age_gen", Math.Max(0, currentEpoch - kv.Value.ClaimedAtEpoch) },
                    { "reason", teardownThrew ? "teardown_threw" : "never_released" }
                };
                leaks.Add(item);
            }
            return leaks;
        }

        public static List<Dictionary<string, object>> SnapshotAll()
        {
            var all = new List<Dictionary<string, object>>();
            foreach (var kv in _slots)
            {
                all.Add(new Dictionary<string, object>
                {
                    { "key", kv.Key },
                    { "owner_guid", kv.Value.BuildGuid },
                    { "claimed_at_epoch", kv.Value.ClaimedAtEpoch }
                });
            }
            all.Sort((a, b) => string.CompareOrdinal((string)a["key"], (string)b["key"]));
            return all;
        }

        public static int Count { get { return _slots.Count; } }
    }
}

