using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPT_EasyChecker_Server;

[Injectable]
public sealed class EasyCheckerBootstrap : IOnLoad
{
    
    private static bool _patched;
    
    private readonly HttpResponseUtil _httpResponseUtil;
    private readonly ISptLogger<EasyCheckerBootstrap> _logger;
    
    public EasyCheckerBootstrap(HttpResponseUtil httpResponseUtil, ISptLogger<EasyCheckerBootstrap> logger)
    {
        _httpResponseUtil = httpResponseUtil;
        _logger = logger;
    }
    
    /**
     * 加载
     */
    public Task OnLoad()
    {
        if (_patched) return Task.CompletedTask;
        
        Guard.Configure(_httpResponseUtil, _logger);
        SqLiteConfigure.Configure(_logger);
        
        // 使用服务端 GUID 作为 Harmony 实例 id
        var harmony = new Harmony(Constants.ServerGuid);

        // 扫描当前程序集内所有 [HarmonyPatch] 类并安装补丁。
        harmony.PatchAll(typeof(EasyCheckerBootstrap).Assembly);

        _patched = true;

        // 日志从常量生成，避免常量改名后日志仍显示旧 GUID。
        _logger.Info(
            $"[MilkAntiCheatExpert] 反作弊初始化完成. Required client plugins: {string.Join(", ", Constants.RequiredClientPluginGuids)}");

        return Task.CompletedTask;
    }
}
