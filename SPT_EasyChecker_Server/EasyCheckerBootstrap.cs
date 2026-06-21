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
    public Task OnLoad()
    {
        if (_patched) return Task.CompletedTask;

        Guard.Configure(_httpResponseUtil, _logger);
        JsonAuditStore.Configure(_logger);
        FikaCrc32Store.Configure(_logger);

        var harmony = new Harmony(Constants.ServerGuid);
        harmony.PatchAll(typeof(EasyCheckerBootstrap).Assembly);

        _patched = true;

        _logger.Info(
            $"[MilkAntiCheatExpert] 反作弊初始化完成. Required client plugins: {string.Join(", ", Constants.RequiredClientPluginGuids)}");

        return Task.CompletedTask;
    }
}
