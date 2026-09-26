using System;
using System.Collections.Generic;
using System.Text;

namespace LoopHost
{

    public sealed class RingLogSink
    {
        private static readonly RingLogSink _instance = new RingLogSink();
        public static RingLogSink Instance { get { return _instance; } }

        private const int Capacity = 8192;

        private readonly object _lock = new object();
        private readonly string[] _lines = new string[Capacity];
        private long _nextSeq;

        private RingLogSink() { }

        public void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                lock (_lock)
                {
                    _lines[_nextSeq % Capacity] = line;
                    _nextSeq++;
                }
            }
            catch { }
        }

        public long CurrentSeq
        {
            get { lock (_lock) { return _nextSeq; } }
        }

        public Dictionary<string, object> ReadSince(long sinceSeq, string runId, int limit)
        {
            if (limit <= 0) limit = 200;
            var result = new Dictionary<string, object>();
            var lines = new List<string>();

            lock (_lock)
            {
                long oldest = _nextSeq > Capacity ? _nextSeq - Capacity : 0;
                long from = sinceSeq > oldest ? sinceSeq : oldest;
                long seq = from;
                for (; seq < _nextSeq && lines.Count < limit; seq++)
                {
                    string line = _lines[seq % Capacity];
                    if (line == null) continue;
                    if (runId != null && line.IndexOf("\"run_id\":\"" + runId + "\"", StringComparison.Ordinal) < 0)
                        continue;
                    lines.Add(line);
                }
                // next_seq = 读位置（下页续读起点），不是环写游标 _nextSeq——
                // 旧语义会让分页读取方跳过 [本页尾, 写游标) 整段日志。
                result["next_seq"] = seq;
            }

            result["lines"] = lines;
            result["dropped"] = sinceSeq < (_nextSeq - Capacity) && sinceSeq >= 0;
            return result;
        }

        public List<string> TailByRunId(string runId, int count)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(runId)) return lines;
            lock (_lock)
            {
                long oldest = _nextSeq > Capacity ? _nextSeq - Capacity : 0;
                for (long seq = _nextSeq - 1; seq >= oldest && lines.Count < count; seq--)
                {
                    string line = _lines[seq % Capacity];
                    if (line != null && line.IndexOf("\"run_id\":\"" + runId + "\"", StringComparison.Ordinal) >= 0)
                        lines.Add(line);
                }
            }
            lines.Reverse();
            return lines;
        }

        public static void Log(string category, string message)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"ts\":\"").Append(DateTime.UtcNow.ToString("o"))
              .Append("\",\"host\":\"").Append(category)
              .Append("\",\"msg\":").Append(MiniJson.Write(message ?? string.Empty))
              .Append('}');
            Instance.Append(sb.ToString());
        }
    }
}

