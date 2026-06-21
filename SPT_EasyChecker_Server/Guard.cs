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
    private const string InvalidNotifierUrl = "http://127.0.0.1:9/anticheat/rejected";
    private const string InvalidWebSocketUrl = "ws://127.0.0.1:9/anticheat/rejected";

    private static readonly ConcurrentDictionary<string, string> RejectedSessions = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/launcher/server/connect",
        "/launcher/ping",
        "/launcher/server/version",
        "/files/launcher/bg.png",
        "/launcher/profiles",
        "/launcher/profile/login",
        "/launcher/profile/register",
        "/launcher/profile/get",
        "/launcher/profile/info",
        "/launcher/server/loadedServerMods",
        "/launcher/server/serverModsUsedBy",
        "/singleplayer/bosstypes",
        "/singleplayer/settings/version",
        "/singleplayer/release",
        "/singleplayer/enableBSGlogging",
        "/singleplayer/moddedTraders",
        "/singleplayer/bundles",
        "/client/menu/locale/en",
        "/client/game/mode",
        "/client/game/start",
        "/singleplayer/clientmods",
        "/fika/natpunchserver/config"
    };

    private static HttpResponseUtil? _httpResponseUtil;
    private static ISptLogger<EasyCheckerBootstrap>? _logger;

    public static void Configure(HttpResponseUtil httpResponseUtil, ISptLogger<EasyCheckerBootstrap> logger)
    {
        _httpResponseUtil = httpResponseUtil;
        _logger = logger;
    }

    public static bool IsRejected(MongoId sessionId, out string reason)
    {
        return RejectedSessions.TryGetValue(sessionId.ToString(), out reason!);
    }

    public static bool IsAllowedRequest(HttpContext context)
    {
        return context.Request.Path.HasValue
               && AllowedPaths.Contains(context.Request.Path.Value);
    }

    public static bool ValidateClientMods(
        SendClientModsRequest? request,
        MongoId sessionId,
        out string rejectionMessage)
    {
        var activeMods = request?.ActiveClientMods;
        if (activeMods is null)
        {
            rejectionMessage = "客户端未正常上报 ChainLoader";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        var loadedGuids = activeMods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.GUID))
            .Select(mod => mod.GUID)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingGuids = Constants.RequiredClientPluginGuids
            .Where(requiredGuid => !loadedGuids.Contains(requiredGuid))
            .ToArray();

        if (missingGuids.Length > 0)
        {
            rejectionMessage = $"客户端缺少 Plugins: {string.Join(", ", missingGuids)}";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        var fikaMod = activeMods.First(mod =>
            string.Equals(mod.GUID, Constants.FikaCoreGuid, StringComparison.OrdinalIgnoreCase));

        if (!FikaCrc32Store.ValidateOrLearn(fikaMod, out rejectionMessage))
        {
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        rejectionMessage = string.Empty;
        MarkVerified(sessionId);
        return true;
    }

    public static string BuildRejectBody(string reason)
    {
        var message = $"AntiCheat rejected this client: {reason}";

        return _httpResponseUtil is null
            ? $$"""{"err":403,"errmsg":"{{message}}","data":null}"""
            : _httpResponseUtil.GetBody<object?>(null, BackendErrorCodes.HTTPForbidden, message);
    }

    public static async Task RejectHttpRequestAsync(
        HttpContext context,
        MongoId sessionId,
        string reason)
    {
        _logger?.Warning(
            $"[MilkAntiCheatExpert] Rejected HTTP request for session {sessionId}: "
            + $"{context.Request.Method} {context.Request.Path} ({reason})");

        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        await context.Response.WriteAsync(BuildRejectBody(reason));
    }

    public static bool TryGetRejectedSessionFromPath(
        HttpContext context,
        out string sessionId,
        out string reason)
    {
        sessionId = string.Empty;
        reason = string.Empty;

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var candidate = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (candidate is null || !RejectedSessions.TryGetValue(candidate, out reason!))
            return false;

        sessionId = candidate;
        return true;
    }

    public static void StampRejectedNotifierChannel(MongoId sessionId, NotifierChannel channel)
    {
        if (!IsRejected(sessionId, out _))
            return;

        channel.NotifierServer = InvalidNotifierUrl;
        channel.WebSocket = InvalidWebSocketUrl;
        channel.Url = string.Empty;
    }

    public static void LogRejectedWebSocket(string sessionId, string reason)
    {
        _logger?.Warning($"[MilkAntiCheatExpert] Blocked websocket for rejected session {sessionId}: {reason}");
    }

    public static async Task CloseRejectedWebSocketAsync(WebSocket socket, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                $"AntiCheat rejected client: {reason}",
                CancellationToken.None);
        }
    }

    private static void MarkVerified(MongoId sessionId)
    {
        var key = sessionId.ToString();

        if (RejectedSessions.TryRemove(key, out _))
            _logger?.Info($"[MilkAntiCheatExpert] Session {key} 在拒绝之后通过验证.");
        else
            _logger?.Info($"[MilkAntiCheatExpert] Session {key} 通过验证.");
    }

    private static void MarkRejected(MongoId sessionId, string reason)
    {
        var key = sessionId.ToString();
        RejectedSessions[key] = reason;
        _logger?.Warning($"[MilkAntiCheatExpert] 拒绝 session {key}: {reason}");
    }
}
