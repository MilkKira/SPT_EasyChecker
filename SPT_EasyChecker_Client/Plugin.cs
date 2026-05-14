using BepInEx;
using BepInEx.Logging;


namespace SPT_EasyChecker_Client
{
    [BepInPlugin("com.milkkira.easychecker.client", "Milkkira", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ManualLogSource Log;
		
        private string officialUIModuleHash = "fb779dd1543296fdf61d1101ff854189";

        private void Awake()
        {
            this.Log = base.Logger;
            this.Log.LogInfo("[MilkAntiCheatExpertClient] 反作弊客户端开始初始化...");
        }
    }
}