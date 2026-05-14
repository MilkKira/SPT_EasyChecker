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


/**
 * 服务端请求处理
 */
[HarmonyPatch(typeof(SptHttpListener), "Handle", typeof(MongoId), typeof(HttpContext))]
internal static class SptHttpListenerHandlePatch
{
    private static bool Prefix(MongoId sessionId, HttpContext context, ref Task __result) {
        
        //通过
        if (!Guard.IsRejected(sessionId, out var reason))
        {
            return true;
        }
        
        //白名单路径
        if (Guard.IsClientModsRequest(context))
        {
            return true;
        }
        
        // 返回 false 表示跳过原始 Handle；
        SqLiteConfigure.RecordHttpRequest(sessionId, context, "HTTP_Rejects", reason);
        __result = Guard.RejectHttpRequestAsync(context, sessionId, reason);
        return false;
    }
}

/**
 * 客户端请求接口 "/singleplayer/clientmods" 处理Patch
 */
[HarmonyPatch(typeof(GameCallbacks), nameof(GameCallbacks.ReceiveClientMods))]
internal static class ReceiveClientModsPatch
{
    private static bool Prefix(SendClientModsRequest request, MongoId sessionID, ref ValueTask<string> __result) {
        
        //模组验证
        if (Guard.ValidateClientMods(request, sessionID, out var rejectionMessage))
        {
            return true;
        }
        
        // 返回错误
        __result = new ValueTask<string>(Guard.BuildRejectBody(rejectionMessage));
        return false;
    }
}

/**
 * 防止被拒客户端继续拿到正常 websocket 地址
 */
[HarmonyPatch(typeof(NotifierController), nameof(NotifierController.GetChannel))]
internal static class NotifierChannelPatch
{
    private static void Postfix(MongoId sessionId, NotifierChannel __result)
    {
        Guard.StampRejectedNotifierChannel(sessionId, __result);
    }
}

/**
 * 已拒绝 session 如果继续发起 websocket 连接，就在这里主动关闭，避免保留实时通知。
 */
[HarmonyPatch(typeof(SptWebSocketConnectionHandler), nameof(SptWebSocketConnectionHandler.OnConnection))]
internal static class SptWebSocketConnectionPatch
{
    private static bool Prefix(WebSocket ws, HttpContext context, ref Task __result) {
        
        // 检测是否属于被拒绝的 Session
        if (!Guard.TryGetRejectedSessionFromPath(context, out var sessionId, out var reason))
        {
            return true;
        }
        // 记录并拒绝WS
        Guard.LogRejectedWebSocket(sessionId, reason);
        __result = Guard.CloseRejectedWebSocketAsync(ws, reason);
        return false;
    }
}