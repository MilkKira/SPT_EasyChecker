namespace SPT_EasyChecker_Server;

public sealed record ClientHeartbeatEnvelope(
    string SessionId,
    string ClientGuid,
    string ClientVersion,
    long TimestampUnixSeconds,
    string Nonce,
    ClientIntegrityReport Integrity,
    ClientAntiCheatReport AntiCheat);

public sealed record ClientIntegrityReport(
    bool ClientPluginPresent,
    string ClientPluginPath,
    IReadOnlyDictionary<string, string> FileSha256);

public sealed record ClientAntiCheatReport(
    bool HasCheatEngineProcess,
    bool HasSuspiciousKernelModule,
    bool HasMemoryPatchTool,
    IReadOnlyList<string> Findings);
