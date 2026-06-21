namespace SPT_EasyChecker_Server;

internal static class Constants
{
    public const string ServerGuid = "com.milkkira.easychecker.server";
    public const string ServerPluginName = "牛奶的简单反作弊";
    public const string FikaCoreGuid = "com.fika.core";

    public static readonly string[] RequiredClientPluginGuids =
    [
        FikaCoreGuid
    ];
}
