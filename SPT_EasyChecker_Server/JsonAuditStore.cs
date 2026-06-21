using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPT_EasyChecker_Server;

internal static class JsonAuditStore
{
    private sealed record AccessEvent(
        DateTimeOffset CreatedAt,
        string EventType,
        string Decision,
        string SessionId,
        string? ProfileId,
        string? IpAddress,
        string? ForwardedFor,
        string? Method,
        string? Path,
        string? QueryString,
        string? Host,
        string? UserAgent,
        string? TraceId,
        string? Reason);

    private const int MaxTextLength = 2048;
    private const string AuditDirectoryName = "audit";
    private const string AccessLogFileName = "access-events.jsonl";

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    private static string? _accessLogPath;

    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;

        var modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(modDirectory))
            modDirectory = AppContext.BaseDirectory;

        var auditDirectory = Path.Combine(modDirectory, AuditDirectoryName);
        Directory.CreateDirectory(auditDirectory);

        _accessLogPath = Path.Combine(auditDirectory, AccessLogFileName);
        EnsureFileExists(_accessLogPath);

        _logger.Info($"[MilkAntiCheatExpert] JSON 审计存储已初始化: {_accessLogPath}");
    }

    public static void RecordHttpRequest(
        MongoId sessionId,
        HttpContext context,
        string decision,
        string? reason = null)
    {
        RecordRequest("http_request", sessionId.ToString(), context, decision, reason);
    }

    public static void RecordWebSocketRequest(
        string sessionId,
        HttpContext context,
        string decision,
        string? reason = null)
    {
        RecordRequest("websocket_request", sessionId, context, decision, reason);
    }

    private static void RecordRequest(
        string eventType,
        string sessionId,
        HttpContext context,
        string decision,
        string? reason)
    {
        var accessEvent = new AccessEvent(
            DateTimeOffset.UtcNow,
            eventType,
            Limit(decision) ?? string.Empty,
            Limit(sessionId) ?? string.Empty,
            ResolveProfileId(sessionId, context),
            Limit(context.Connection.RemoteIpAddress?.ToString()),
            Limit(ReadHeader(context, "X-Forwarded-For")),
            Limit(context.Request.Method),
            Limit(context.Request.Path.Value),
            Limit(context.Request.QueryString.Value),
            Limit(context.Request.Host.Value),
            Limit(ReadHeader(context, "User-Agent")),
            Limit(context.TraceIdentifier),
            Limit(reason));

        Append(accessEvent);
    }

    private static void Append(AccessEvent accessEvent)
    {
        var path = _accessLogPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var json = JsonSerializer.Serialize(accessEvent, JsonOptions);

            lock (SyncRoot)
            {
                File.AppendAllText(path, json + Environment.NewLine);
            }
        }
        catch (Exception exception)
        {
            _logger?.Warning($"[MilkAntiCheatExpert] JSON 审计写入失败: {exception.Message}");
        }
    }

    private static void EnsureFileExists(string path)
    {
        lock (SyncRoot)
        {
            if (File.Exists(path))
                return;

            using var _ = File.Create(path);
        }
    }

    private static string? ResolveProfileId(string sessionId, HttpContext context)
    {
        return Limit(
            ReadHeader(context, "X-Profile-Id")
            ?? ReadHeader(context, "Profile-Id")
            ?? ReadQuery(context, "profileId")
            ?? ReadQuery(context, "profile_id")
            ?? sessionId);
    }

    private static string? ReadHeader(HttpContext context, string name)
    {
        return context.Request.Headers.TryGetValue(name, out var value)
            ? value.ToString()
            : null;
    }

    private static string? ReadQuery(HttpContext context, string name)
    {
        return context.Request.Query.TryGetValue(name, out var value)
            ? value.ToString()
            : null;
    }

    private static string? Limit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= MaxTextLength
            ? trimmed
            : trimmed[..MaxTextLength];
    }
}
