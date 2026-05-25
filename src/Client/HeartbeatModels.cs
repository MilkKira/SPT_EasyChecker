using System.Collections.Generic;

namespace SPT_EasyChecker_Client
{
    internal sealed class ClientHeartbeatEnvelope
    {
        public string SessionId;
        public string ClientGuid;
        public string ClientVersion;
        public long TimestampUnixSeconds;
        public string Nonce;
        public ClientIntegrityReport Integrity;
        public ClientAntiCheatReport AntiCheat;
    }

    internal sealed class ClientIntegrityReport
    {
        public bool ClientPluginPresent;
        public string ClientPluginPath;
        public Dictionary<string, string> FileSha256;
    }

    internal sealed class ClientAntiCheatReport
    {
        public bool HasCheatEngineProcess;
        public bool HasSuspiciousKernelModule;
        public bool HasMemoryPatchTool;
        public List<string> Findings;
    }
}
