using System;

namespace SamplePlugin
{
    /// <summary>
    /// LoopHost 集成契约入口：宿主字节重载业务代后，按类型全名反射定位本类并调用 Register。
    /// 宿主先探测 5 参签名（带 generation slot 认领），不存在则回落 3 参旧签名。
    ///   · register(name, handler)：把业务工具挂进宿主 ToolRegistry（name 即引擎工具名）
    ///   · setTeardown(action)：新业务代注册前宿主会调用上一代的 teardown，在此释放资源
    ///   · attachLogSink()：返回宿主环形日志汇，业务把关键行灌进环，引擎 tail_log 可见
    /// 同一程序集重复 Register（版本未变的 no-op 重载）不得重复挂汇——静态标记随程序集走。
    /// </summary>
    public static class LoopToolRegistration
    {
        private static bool _sinkAttached;

        public static void Register(
            Action<string, Func<string, string>> register,
            Action<Action> setTeardown,
            Func<Action<string>> attachLogSink)
        {
            register("act.run_command", SampleCommands.RunCommand);
            register("query.list_commands", SampleCommands.ListCommands);
            setTeardown(null); // 示例无资源可释放
            if (_sinkAttached) return;
            _sinkAttached = true;
            SampleCommands.RingSink = attachLogSink();
        }
    }
}
