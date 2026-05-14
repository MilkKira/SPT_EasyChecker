namespace SPT_EasyChecker_Server;


// 统一保存服务端和客户端都需要识别的 GUID。
internal static class Constants
{
    public const string ServerGuid = "com.milkkira.easychecker.server";
    
    public const string ServerPluginName = "牛奶的简单反作弊";
    
    public const string ClientGuid = "com.milkkira.easychecker.client";
    
    public const string FikaCoreGuid = "com.fika.core";
    
    // 服务端强制要求客户端加载的插件列表。
    public static readonly string[] RequiredClientPluginGuids =
    [
        FikaCoreGuid
    ];
}