using System;
using System.Collections.Generic;
using System.IO;

namespace LoopHost
{
    /// <summary>
    /// 宿主侧配置：LoopHost.dll 同目录的 loop.config.json（与引擎侧同名同源，
    /// 引擎读 exe 旁文件，宿主读 dll 旁文件——NETLOAD 后 cwd 不可靠）。
    /// 业务插件身份两项缺省保持内置约定，未配置时行为与硬编码版完全一致。
    /// </summary>
    public static class HostConfig
    {
        private static readonly Dictionary<string, object> Map = Load();

        private static Dictionary<string, object> Load()
        {
            try
            {
                var file = Path.Combine(
                    Path.GetDirectoryName(typeof(HostConfig).Assembly.Location) ?? ".",
                    "loop.config.json");
                if (File.Exists(file))
                {
                    var parsed = MiniJson.ParseObject(File.ReadAllText(file));
                    if (parsed != null) return parsed;
                }
            }
            catch (Exception) { }
            return new Dictionary<string, object>();
        }

        public static string Get(string key, string fallback)
        {
            object v;
            var s = Map.TryGetValue(key, out v) ? v as string : null;
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }
    }
}
