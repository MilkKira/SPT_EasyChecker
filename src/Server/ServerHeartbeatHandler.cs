using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace SPT_EasyChecker_Server;

internal static class ServerHeartbeatHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static ISptLogger<EasyCheckerBootstrap>? _logger;

    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;
    }

    public static bool IsHeartbeatRequest(HttpContext context)
    {
        return context.Request.Path.Equals(Constants.HeartbeatPath, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task HandleAsync(HttpContext context)
    {
        ClientHeartbeatEnvelope? heartbeat = null;
        string? rejectionReason = null;

        try
        {
            heartbeat = await ReadHeartbeatAsync(context);
            if (heartbeat is null)
            {
                rejectionReason = "心跳载荷无效";
                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { ok = false, reason = rejectionReason });
                return;
            }

            if (string.IsNullOrWhiteSpace(heartbeat.SessionId) || !MongoId.IsValidMongoId(heartbeat.SessionId))
            {
                rejectionReason = "心跳缺少有效 session id";
                await WriteJsonAsync(context, StatusCodes.Status403Forbidden, new { ok = false, reason = rejectionReason });
                return;
            }

            var sessionId = new MongoId(heartbeat.SessionId);
            if (!Guard.ValidateHeartbeat(sessionId, heartbeat, out rejectionReason))
            {
                await WriteJsonAsync(context, StatusCodes.Status403Forbidden, new { ok = false, reason = rejectionReason });
                return;
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true, nextHeartbeatSeconds = Guard.HeartbeatIntervalSeconds });
        }
        catch (JsonException exception)
        {
            rejectionReason = $"心跳 JSON 无效: {exception.Message}";
            _logger?.Warning($"[MilkAntiCheatExpert] {rejectionReason}");
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { ok = false, reason = rejectionReason });
        }
        finally
        {
            if (heartbeat is not null)
                JsonAuditStore.RecordHeartbeat(heartbeat, rejectionReason is null ? "Heartbeat_Accepted" : "Heartbeat_Rejected", rejectionReason);
        }
    }

    public static async Task HandleAsync(HttpContext context, MongoId sessionId)
    {
        ClientHeartbeatEnvelope? heartbeat = null;
        string? rejectionReason = null;

        try
        {
            heartbeat = await ReadHeartbeatAsync(context);

            if (heartbeat is not null && string.IsNullOrWhiteSpace(heartbeat.SessionId))
                heartbeat = heartbeat with { SessionId = sessionId.ToString() };

            if (heartbeat is null || !Guard.ValidateHeartbeat(sessionId, heartbeat, out rejectionReason))
            {
                await WriteJsonAsync(context, StatusCodes.Status403Forbidden, new { ok = false, reason = rejectionReason ?? "心跳载荷无效" });
                return;
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true, nextHeartbeatSeconds = Guard.HeartbeatIntervalSeconds });
        }
        catch (JsonException exception)
        {
            rejectionReason = $"心跳 JSON 无效: {exception.Message}";
            Guard.MarkRejected(sessionId, rejectionReason);
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new { ok = false, reason = rejectionReason });
        }
        finally
        {
            if (heartbeat is not null)
                JsonAuditStore.RecordHeartbeat(heartbeat, rejectionReason is null ? "Heartbeat_Accepted" : "Heartbeat_Rejected", rejectionReason);
        }
    }

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, object body)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(body, JsonOptions, context.RequestAborted);
    }

    private static Task<ClientHeartbeatEnvelope?> ReadHeartbeatAsync(HttpContext context)
    {
        return JsonSerializer.DeserializeAsync<ClientHeartbeatEnvelope>(
                context.Request.Body,
                JsonOptions,
                context.RequestAborted)
            .AsTask();
    }
}
