using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Models.Eft.Notifier;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SPT_EasyChecker_Server;

internal static partial class Guard
{
    private sealed record ClientSessionState(
        DateTimeOffset LastHeartbeatAt,
        IReadOnlyDictionary<string, string> BaselineSha256);

    private const string InvalidNotifierUrl = "http://127.0.0.1:9/anticheat/rejected";
    private const string InvalidWebSocketUrl = "ws://127.0.0.1:9/anticheat/rejected";
    private const int HeartbeatGraceSeconds = 45;

    public const int HeartbeatIntervalSeconds = 15;

    private static readonly ConcurrentDictionary<string, string> RejectedSessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> VerifiedSessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ClientSessionState> Heartbeats = new(StringComparer.Ordinal);

    private static readonly Regex Sha256Regex = CreateSha256Regex();

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

    public static bool IsPreHeartbeatAllowedRequest(HttpContext context)
    {
        if (!context.Request.Path.HasValue) return false;

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path)) return false;

        return RequestWhitelistStore.IsPreHeartbeatAllowed(context);
    }

    public static bool ValidateClientMods(SendClientModsRequest? request, MongoId sessionId, out string rejectionMessage)
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

        var missing = Constants.RequiredClientPluginGuids
            .Where(requiredGuid => !loadedGuids.Contains(requiredGuid))
            .ToArray();

        if (missing.Length == 0)
        {
            MarkVerified(sessionId);
            rejectionMessage = string.Empty;
            return true;
        }

        rejectionMessage = $"客户端缺少 Plugins: {string.Join(", ", missing)}";
        MarkRejected(sessionId, rejectionMessage);
        return false;
    }

    public static bool ValidateHeartbeat(MongoId sessionId, ClientHeartbeatEnvelope heartbeat, out string rejectionMessage)
    {
        var key = sessionId.ToString();
        rejectionMessage = string.Empty;

        if (!string.Equals(heartbeat.ClientGuid, Constants.ClientGuid, StringComparison.OrdinalIgnoreCase))
        {
            rejectionMessage = "客户端心跳 GUID 不匹配";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        if (heartbeat.Integrity is null || heartbeat.Integrity.FileSha256 is null)
        {
            rejectionMessage = "客户端未上报完整性信息";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        if (!heartbeat.Integrity.ClientPluginPresent)
        {
            rejectionMessage = "客户端插件文件不存在";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        foreach (var requiredFile in Constants.RequiredIntegrityFiles)
        {
            if (!heartbeat.Integrity.FileSha256.TryGetValue(requiredFile, out var hash) || !Sha256Regex.IsMatch(hash))
            {
                rejectionMessage = $"客户端缺少完整性哈希: {requiredFile}";
                MarkRejected(sessionId, rejectionMessage);
                return false;
            }

            if (TrustedIntegrityStore.HasRules && !TrustedIntegrityStore.IsTrusted(requiredFile, hash))
            {
                rejectionMessage = $"客户端文件哈希不在白名单: {requiredFile}";
                MarkRejected(sessionId, rejectionMessage);
                return false;
            }
        }

        if (heartbeat.AntiCheat is null)
        {
            rejectionMessage = "客户端未上报反作弊检测结果";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        if (heartbeat.AntiCheat.HasCheatEngineProcess || heartbeat.AntiCheat.HasSuspiciousKernelModule || heartbeat.AntiCheat.HasMemoryPatchTool)
        {
            rejectionMessage = $"客户端命中反作弊检测: {string.Join(", ", heartbeat.AntiCheat.Findings ?? [])}";
            MarkRejected(sessionId, rejectionMessage);
            return false;
        }

        if (Heartbeats.TryGetValue(key, out var previous))
        {
            foreach (var (fileName, baselineHash) in previous.BaselineSha256)
            {
                if (!heartbeat.Integrity.FileSha256.TryGetValue(fileName, out var currentHash)
                    || !string.Equals(currentHash, baselineHash, StringComparison.OrdinalIgnoreCase))
                {
                    rejectionMessage = $"客户端文件完整性变化: {fileName}";
                    MarkRejected(sessionId, rejectionMessage);
                    return false;
                }
            }
        }

        Heartbeats[key] = new ClientSessionState(DateTimeOffset.UtcNow, new Dictionary<string, string>(heartbeat.Integrity.FileSha256, StringComparer.OrdinalIgnoreCase));
        MarkVerified(sessionId);
        return true;
    }

    public static bool HasFreshHeartbeat(MongoId sessionId, out string rejectionMessage)
    {
        rejectionMessage = string.Empty;
        var key = sessionId.ToString();

        if (!Heartbeats.TryGetValue(key, out var heartbeat))
        {
            rejectionMessage = "客户端心跳不存在";
            return false;
        }

        if (DateTimeOffset.UtcNow - heartbeat.LastHeartbeatAt <= TimeSpan.FromSeconds(HeartbeatGraceSeconds))
            return true;

        rejectionMessage = "客户端心跳超时";
        MarkRejected(sessionId, rejectionMessage);
        return false;
    }

    public static string BuildRejectBody(string reason)
    {
        var message = $"AntiCheat rejected this client: {reason}";

        if (_httpResponseUtil is null) return $$"""{"err":403,"errmsg":"{{message}}","data":null}""";

        return _httpResponseUtil.GetBody<object?>(null, BackendErrorCodes.HTTPForbidden, message);
    }

    public static void MarkRejected(MongoId sessionId, string reason)
    {
        var key = sessionId.ToString();

        VerifiedSessions.TryRemove(key, out _);
        RejectedSessions[key] = reason;
        _logger?.Warning($"[MilkAntiCheatExpert] 拒绝 session {key}: {reason}");
    }

    public static async Task RejectHttpRequestAsync(HttpContext context, MongoId sessionId, string reason)
    {
        var sessionKey = sessionId.ToString();

        _logger?.Warning(
            $"[MilkAntiCheatExpert] Rejected HTTP request for session {sessionKey}: {context.Request.Method} {context.Request.Path} ({reason})");

        if (context.Response.HasStarted) return;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        await context.Response.WriteAsync(BuildRejectBody(reason));
    }

    public static bool TryGetRejectedSessionFromPath(HttpContext context, out string sessionId, out string reason)
    {
        sessionId = string.Empty;
        reason = string.Empty;

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var candidate = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (candidate is null || !RejectedSessions.TryGetValue(candidate, out reason!)) return false;

        sessionId = candidate;
        return true;
    }

    public static void StampRejectedNotifierChannel(MongoId sessionId, NotifierChannel channel)
    {
        if (!IsRejected(sessionId, out _)) return;

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
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                $"AntiCheat rejected client: {reason}",
                CancellationToken.None);
    }

    private static void MarkVerified(MongoId sessionId)
    {
        var key = sessionId.ToString();
        VerifiedSessions.TryAdd(key, 0);

        if (RejectedSessions.TryRemove(key, out _))
            _logger?.Info($"[MilkAntiCheatExpert] Session {key} 在拒绝之后通过验证.");
        else
            _logger?.Info($"[MilkAntiCheatExpert] Session {key} 通过验证.");
    }

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex CreateSha256Regex();
}
