using System.Net.WebSockets;
using HarmonyLib;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Servers.Ws;

namespace SPT_EasyChecker_Server;


// 服务端请求处理Patch
[HarmonyPatch(typeof(SptHttpListener), "Handle", typeof(MongoId), typeof(HttpContext))]
internal static class SptHttpListenerHandlePatch
{
    private static bool Prefix(MongoId sessionId, HttpContext context, ref Task __result) {
        return false;
    }
}

// 客户端请求接口 "/singleplayer/clientmods" 处理Patch
[HarmonyPatch(typeof(GameCallbacks), nameof(GameCallbacks.ReceiveClientMods))]
internal static class ReceiveClientModsPatch
{
    private static bool Prefix(SendClientModsRequest request, MongoId sessionID, ref ValueTask<string> __result) {
        return false;
    }
}



// WebSocket 兜底：长连接入口不一定会完整经过普通 HTTP 路由处理。
// 已拒绝 session 如果继续发起 websocket 连接，就在这里主动关闭，避免保留实时通知/联机通道。
[HarmonyPatch(typeof(SptWebSocketConnectionHandler), nameof(SptWebSocketConnectionHandler.OnConnection))]
internal static class SptWebSocketConnectionPatch
{
    private static bool Prefix(WebSocket ws, HttpContext context, ref Task __result) {

        return false;
    }
}