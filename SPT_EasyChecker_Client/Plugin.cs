using BepInEx;
using BepInEx.Logging;

namespace SPT_EasyChecker_Client
{
    [BepInPlugin("com.milkkira.easychecker.client", "Milkkira", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private ManualLogSource _log;

        private void Awake()
        {
            _log = Logger;
            _log.LogInfo("[MilkAntiCheatExpertClient] MACE Init...");
        }
    }
}
