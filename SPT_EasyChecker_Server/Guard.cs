using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Models.Eft.Notifier;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPT_EasyChecker_Server;

internal static class Guard
{
    // 这两个地址设置的客户端本机回环死地址。
    // 服务器把地址写进 NotifierChannel 后，真正尝试连接的是玩家客户端；因此 127.0.0.1 指玩家自己的电脑，不是云服务器。
    // 端口 9 通常没有服务监听，可以让被拒绝客户端快速连接失败，作为 HTTP 全局拒绝之外的兜底。
    private const string InvalidNotifierUrl = "http://127.0.0.1:9/anticheat/rejected";
    private const string InvalidWebSocketUrl = "ws://127.0.0.1:9/anticheat/rejected";
    
    // 字典值保存拒绝原因，便于后续 403 响应和日志输出给出一致信息。
    private static readonly ConcurrentDictionary<string, string> RejectedSessions = new(StringComparer.Ordinal);
    
    // 决定拦截的是 RejectedSessions，VerifiedSessions 主要服务日志和后续扩展。
    private static readonly ConcurrentDictionary<string, byte> VerifiedSessions = new(StringComparer.Ordinal);
    
    private static HttpResponseUtil? _httpResponseUtil;
    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    
    //白名单路径
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/singleplayer/clientmods"
    };
    
    /**
     * 配置方法函数
     */
    public static void Configure(HttpResponseUtil httpResponseUtil, ISptLogger<EasyCheckerBootstrap> logger) 
    {
        // 这两个对象来自 SPT 的 DI 容器。集中保存后，静态 Harmony patch 就能统一生成 SPT 风格响应和日志。
        _httpResponseUtil = httpResponseUtil;
        _logger = logger;
    }
    
    /**
     * 请求的客户端是否被拒绝
     */
    public static bool IsRejected(MongoId sessionId, out string reason)
    {
        // 所有入口统一通过这个方法判断 session 是否已经进入拒绝名单。
        return RejectedSessions.TryGetValue(sessionId.ToString(), out reason!);
    }
    
    /**
     * 白名单路径
     */
    public static bool IsClientModsRequest(HttpContext context)
    {
        // 只给 clientmods 留恢复通道，其它接口一旦 session 被拒绝就不再进入 SPT 路由。
        // return context.Request.Path.Equals(ClientModsPath, StringComparison.OrdinalIgnoreCase);
        
        return IsAllowedPath(context);
    }
    
    /**
     * private 检测是否是白名单路径
     */
    private static bool IsAllowedPath(HttpContext context)
    {
        if (context.Request.Path.HasValue)
            return AllowedPaths.Contains(context.Request.Path.Value);
        return false;
    }
    
    /**
     * 验证客户端模组函数
     */
    public static bool ValidateClientMods(SendClientModsRequest? request, MongoId sessionId, out string rejectionMessage) 
    {
        // Fika 会通过 clientmods 请求携带客户端 BepInEx 插件列表。
        // 如果请求体或 ActiveClientMods 缺失，说明客户端没有正确上报，按不可信处理。
        var activeMods = request?.ActiveClientMods;
        if (activeMods is null)
        {
            rejectionMessage = "客户端未正常上报ChainLoader";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        // GUID 不区分大小写，比对时统一放入 HashSet，避免因为大小写差异造成误拒绝。
        var loadedGuids = activeMods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.GUID))
            .Select(mod => mod.GUID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // RequiredClientPluginGuids 是服务端唯一的强制客户端插件清单。
        var missing = Constants.RequiredClientPluginGuids
            .Where(requiredGuid => !loadedGuids.Contains(requiredGuid))
            .ToArray();

        if (missing.Length == 0)
        {
            // 允许客户端重新上报后恢复：如果之前被拒绝，MarkVerified 会清除 RejectedSessions。
            MarkVerified(sessionId);
            rejectionMessage = string.Empty;
            return true;
        }

        rejectionMessage = $"客户端缺少Plugins: {string.Join(", ", missing)}";
        MarkRejected(sessionId, rejectionMessage);
        return false;
    }
    
    /**
     * private 构建请求拒绝体
     */
    private static string BuildRejectBody(string reason)
    {
        var message = $"AntiCheat rejected this client: {reason}";

        // 优先使用 SPT 自带 HttpResponseUtil 生成标准错误包，客户端更容易按原有逻辑处理。
        // 兜底 JSON 用于极早期或测试场景，避免 DI 尚未注入时因为 null 直接抛异常。
        if (_httpResponseUtil is null) return $$"""{"err":403,"errmsg":"{{message}}","data":null}""";

        return _httpResponseUtil.GetBody<object?>(null, BackendErrorCodes.HTTPForbidden, message);
    }
    
    /**
     * private 标记通过
     */
    private static void MarkVerified(MongoId sessionId)
    {
        var key = sessionId.ToString();
        VerifiedSessions.TryAdd(key, 0);

        // 支持“修复客户端后重新上报”：通过校验时移除旧拒绝记录，不需要重启服务器。
        if (RejectedSessions.TryRemove(key, out _))
            _logger?.Info($"[MilkAntiCheatExpert] Session {key} 在拒绝之后通过验证.");
        else
            _logger?.Info($"[MilkAntiCheatExpert] Session {key} 通过验证.");
    }
    
    /**
     * private 标记拒绝
     */
    private static void MarkRejected(MongoId sessionId, string reason)
    {
        var key = sessionId.ToString();

        // 一旦拒绝，VerifiedSessions 中的旧通过状态必须清理，避免状态互相矛盾。
        VerifiedSessions.TryRemove(key, out _);
        RejectedSessions[key] = reason;
        _logger?.Warning($"[MilkAntiCheatExpert] 拒绝 session {key}: {reason}");
    }
    
    
    /**
     * 异步拦截HTTP请求
     */
    public static async Task RejectHttpRequestAsync(HttpContext context, MongoId sessionId, string reason)
    {
        var sessionKey = sessionId.ToString();
        

        _logger?.Warning(
            $"[MilkAntiCheatExpert] Rejected HTTP request for session {sessionKey}: {context.Request.Method} {context.Request.Path} ({reason})");

        if (context.Response.HasStarted) return;

        // 在 HTTP listener 边界直接结束请求，避免进入 SPT 后续路由/控制器。
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        await context.Response.WriteAsync(BuildRejectBody(reason));
    }
    
    /**
     * 请求路径中取最后一段 session id
     *
     * TODO:未来可能变更方法
     */
    public static bool TryGetRejectedSessionFromPath(HttpContext context, out string sessionId, out string reason)
    {
        sessionId = string.Empty;
        reason = string.Empty;

        // WebSocket 连接没有直接传 MongoId 参数，只能从请求路径中取最后一段 session id。
        // 如果未来 SPT 改了 WebSocket 路径格式，这里是优先需要检查的兼容点。
        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var candidate = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (candidate is null || !RejectedSessions.TryGetValue(candidate, out reason!)) return false;

        sessionId = candidate;
        return true;
    }
    
    /**
     * 如果被拒绝客户端仍然走到 NotifierChannel 获取阶段，就把通知/长连接地址改成死地址。
     */
    public static void StampRejectedNotifierChannel(MongoId sessionId, NotifierChannel channel)
    {
        if (!IsRejected(sessionId, out _)) return;

        //针对“已提前拿到 channel”做最后防御
        channel.NotifierServer = InvalidNotifierUrl;
        channel.WebSocket = InvalidWebSocketUrl;
        channel.Url = string.Empty;
    }
    
    /**
     * 日志输出被拒绝的WS
     */
    public static void LogRejectedWebSocket(string sessionId, string reason)
    {
        _logger?.Warning($"[MilkAntiCheatExpert] Blocked websocket for rejected session {sessionId}: {reason}");
    }
    
    /**
     * 异步拦截WS
     */
    public static async Task CloseRejectedWebSocketAsync(WebSocket socket, string reason)
    {
        // 主动关闭已拒绝 session 的 WebSocket，让客户端长连接侧也得到明确的策略拒绝。
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                $"AntiCheat rejected client: {reason}",
                CancellationToken.None);
    }
    
}