using BepInEx;
using BepInEx.Logging;


namespace SPT_EasyChecker_Client
{
    [BepInPlugin("com.milkkira.easychecker.client", "Milkkira", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ManualLogSource Log;
        private void Awake()
        {
            this.Log = base.Logger;
            this.Log.LogInfo("[MilkAntiCheatExpertClient] MACE Init...");
        }
    }
}