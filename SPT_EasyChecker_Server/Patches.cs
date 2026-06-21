using System.Net.WebSockets;
using HarmonyLib;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Models.Eft.Notifier;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Servers.Ws;

namespace SPT_EasyChecker_Server;

[HarmonyPatch(typeof(SptHttpListener), "Handle", typeof(MongoId), typeof(HttpContext))]
internal static class SptHttpListenerHandlePatch
{
    private static bool Prefix(MongoId sessionId, HttpContext context, ref Task __result)
    {
        if (!Guard.IsRejected(sessionId, out var reason))
            return true;

        if (Guard.IsAllowedRequest(context))
            return true;

        JsonAuditStore.RecordHttpRequest(sessionId, context, "HTTP_Rejected", reason);
        __result = Guard.RejectHttpRequestAsync(context, sessionId, reason);
        return false;
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
        JsonAuditStore.RecordWebSocketRequest(sessionId, context, "WebSocket_Rejected", reason);
        __result = Guard.CloseRejectedWebSocketAsync(ws, reason);
        return false;
    }
}
