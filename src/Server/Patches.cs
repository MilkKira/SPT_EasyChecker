using System.Net.WebSockets;
using HarmonyLib;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Models.Eft.Notifier;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Servers.Ws;

namespace SPT_EasyChecker_Server;

[HarmonyPatch(typeof(HttpServer), "HandleRequest")]
internal static class HttpServerHandleRequestPatch
{
    private static bool Prefix(HttpContext context, ref Task __result)
    {
        if (!ServerHeartbeatHandler.IsHeartbeatRequest(context))
            return true;

        __result = ServerHeartbeatHandler.HandleAsync(context);
        return false;
    }
}

[HarmonyPatch(typeof(SptHttpListener), "Handle", typeof(MongoId), typeof(HttpContext))]
internal static class SptHttpListenerHandlePatch
{
    private static bool Prefix(MongoId sessionId, HttpContext context, ref Task __result)
    {
        if (ServerHeartbeatHandler.IsHeartbeatRequest(context))
        {
            __result = ServerHeartbeatHandler.HandleAsync(context, sessionId);
            return false;
        }

        if (Guard.IsRejected(sessionId, out var reason))
        {
            if (Guard.IsPreHeartbeatAllowedRequest(context)) return true;

            JsonAuditStore.RecordHttpRequest(sessionId, context, "HTTP_Rejected", reason);
            __result = Guard.RejectHttpRequestAsync(context, sessionId, reason);
            return false;
        }

        if (!Guard.IsPreHeartbeatAllowedRequest(context) && !Guard.HasFreshHeartbeat(sessionId, out var heartbeatReason))
        {
            JsonAuditStore.RecordHttpRequest(sessionId, context, "HTTP_Rejected", heartbeatReason);
            __result = Guard.RejectHttpRequestAsync(context, sessionId, heartbeatReason);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(GameCallbacks), nameof(GameCallbacks.ReceiveClientMods))]
internal static class ReceiveClientModsPatch
{
    private static bool Prefix(SendClientModsRequest request, MongoId sessionID, ref ValueTask<string> __result)
    {
        if (Guard.ValidateClientMods(request, sessionID, out var rejectionMessage))
            return true;

        __result = new ValueTask<string>(Guard.BuildRejectBody(rejectionMessage));
        return false;
    }
}

[HarmonyPatch(typeof(NotifierController), nameof(NotifierController.GetChannel))]
internal static class NotifierChannelPatch
{
    private static void Postfix(MongoId sessionId, NotifierChannel __result)
    {
        Guard.StampRejectedNotifierChannel(sessionId, __result);
    }
}

[HarmonyPatch(typeof(SptWebSocketConnectionHandler), nameof(SptWebSocketConnectionHandler.OnConnection))]
internal static class SptWebSocketConnectionPatch
{
    private static bool Prefix(WebSocket ws, HttpContext context, ref Task __result)
    {
        if (!Guard.TryGetRejectedSessionFromPath(context, out var sessionId, out var reason))
            return true;

        Guard.LogRejectedWebSocket(sessionId, reason);
        JsonAuditStore.RecordWebSocketRequest(context, sessionId, "WebSocket_Rejected", reason);
        __result = Guard.CloseRejectedWebSocketAsync(ws, reason);
        return false;
    }
}
