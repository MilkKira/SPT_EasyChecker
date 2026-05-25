using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPT_EasyChecker_Server;

public static class JsonAuditStore
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
    private const string LogDirectoryName = "audit";
    private const string AccessLogFileName = "access-events.jsonl";
    private const string HeartbeatLogFileName = "heartbeats.jsonl";

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    private static string? _accessLogPath;
    private static string? _heartbeatLogPath;
    private static bool _initialized;

    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var baseDirectory = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppContext.BaseDirectory
            : assemblyDirectory;

        var logDirectory = Path.Combine(baseDirectory, LogDirectoryName);
        Directory.CreateDirectory(logDirectory);

        _accessLogPath = Path.Combine(logDirectory, AccessLogFileName);
        _heartbeatLogPath = Path.Combine(logDirectory, HeartbeatLogFileName);

        lock (SyncRoot)
        {
            EnsureJsonLinesFile(_accessLogPath);
            EnsureJsonLinesFile(_heartbeatLogPath);
            _initialized = true;
        }

        _logger?.Info($"[MilkAntiCheatExpert] JSON 审计日志初始化完成，目录: {logDirectory}");
    }

    public static void RecordWebSocketRequest(HttpContext context, string sessionId, string decision, string? reason = null)
    {
        EnqueueAccessEvent(
            new AccessEvent(
                DateTimeOffset.UtcNow,
                "websocket_request",
                decision,
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
                Limit(reason)));
    }

    public static void RecordHttpRequest(MongoId sessionId, HttpContext context, string decision, string? reason = null)
    {
        var sessionKey = sessionId.ToString();

        EnqueueAccessEvent(
            new AccessEvent(
                DateTimeOffset.UtcNow,
                "http_request",
                decision,
                sessionKey,
                ResolveProfileId(sessionKey, context),
                Limit(context.Connection.RemoteIpAddress?.ToString()),
                Limit(ReadHeader(context, "X-Forwarded-For")),
                Limit(context.Request.Method),
                Limit(context.Request.Path.Value),
                Limit(context.Request.QueryString.Value),
                Limit(context.Request.Host.Value),
                Limit(ReadHeader(context, "User-Agent")),
                Limit(context.TraceIdentifier),
                Limit(reason)));
    }

    public static void RecordHeartbeat(ClientHeartbeatEnvelope heartbeat, string decision, string? reason)
    {
        var entry = new
        {
            createdAt = DateTimeOffset.UtcNow,
            eventType = "client_heartbeat",
            decision,
            sessionId = heartbeat.SessionId,
            heartbeat.ClientGuid,
            heartbeat.ClientVersion,
            heartbeat.TimestampUnixSeconds,
            heartbeat.Nonce,
            heartbeat.Integrity,
            heartbeat.AntiCheat,
            reason = Limit(reason)
        };

        EnqueueJsonLine(_heartbeatLogPath, entry, "heartbeat");
    }

    private static void EnqueueAccessEvent(AccessEvent accessEvent)
    {
        EnqueueJsonLine(_accessLogPath, accessEvent, "access");
    }

    private static void EnqueueJsonLine<T>(string? path, T value, string label)
    {
        _ = Task.Run(
            () =>
            {
                try
                {
                    if (!_initialized || string.IsNullOrWhiteSpace(path)) return;

                    var json = JsonSerializer.Serialize(value, JsonOptions);
                    lock (SyncRoot)
                    {
                        File.AppendAllText(path, json + Environment.NewLine);
                    }
                }
                catch (Exception exception)
                {
                    _logger?.Warning($"[MilkAntiCheatExpert] Failed to write JSON {label} event: {exception.Message}");
                }
            });
    }

    private static void EnsureJsonLinesFile(string path)
    {
        if (File.Exists(path)) return;
        using var _ = File.Create(path);
    }

    private static string? ReadHeader(HttpContext context, string name)
    {
        return context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    private static string? ReadQuery(HttpContext context, string name)
    {
        return context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;
    }

    private static string? Limit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }

    private static string? ResolveProfileId(string sessionId, HttpContext context)
    {
        var profileId =
            ReadHeader(context, "X-Profile-Id")
            ?? ReadHeader(context, "Profile-Id")
            ?? ReadQuery(context, "profileId")
            ?? ReadQuery(context, "profile_id")
            ?? sessionId;

        return Limit(profileId);
    }
}
