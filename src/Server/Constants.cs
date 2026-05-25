namespace SPT_EasyChecker_Server;

// 统一保存服务端和客户端都需要识别的 GUID 与协议常量。
internal static class Constants
{
    public const string ServerGuid = "com.milkkira.easychecker.server";

    public const string ServerPluginName = "牛奶的简单反作弊";

    public const string ClientGuid = "com.milkkira.easychecker.client";

    public const string FikaCoreGuid = "com.fika.core";

    public const string FuckTTPlugins = "com.milkkira.FuckTT";

    public const string HeartbeatPath = "/easychecker/heartbeat";

    public const string FikaCoreFileName = "Fika.Core.dll";

    public const string ClientPluginFileName = "SPT_EasyChecker_Client.dll";

    public static readonly string[] RequiredClientPluginGuids =
    [
        FikaCoreGuid,
        ClientGuid
    ];

    public static readonly string[] RequiredIntegrityFiles =
    [
        FikaCoreFileName,
        ClientPluginFileName
    ];
}
