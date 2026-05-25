using System;
using System.Collections;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine.Networking;

namespace SPT_EasyChecker_Client
{
    [BepInPlugin(ClientConstants.ClientGuid, ClientConstants.ClientName, ClientConstants.ClientVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const float InitialHeartbeatDelaySeconds = 2f;
        private const float FinalHeartbeatTimeoutSeconds = 3f;

        private ManualLogSource _log;
        private ConfigEntry<string> _serverBaseUrl;
        private ConfigEntry<int> _heartbeatIntervalSeconds;
        private ConfigEntry<bool> _allowUntrustedServerCertificate;
        private string _heartbeatUrl;
        private string _sessionId;

        private readonly IntegrityReporter _integrityReporter = new IntegrityReporter();
        private readonly AntiCheatScanner _antiCheatScanner = new AntiCheatScanner();

        private void Awake()
        {
            _log = Logger;
            _serverBaseUrl = Config.Bind(
                "Server",
                "ServerBaseUrl",
                string.Empty,
                "Fallback remote SPT/Fika server base URL. Launcher config SPT/user/launcher/config.json Url is used first.");
            _heartbeatIntervalSeconds = Config.Bind(
                "Server",
                "HeartbeatIntervalSeconds",
                15,
                "Seconds between anti-cheat heartbeats.");
            _allowUntrustedServerCertificate = Config.Bind(
                "Server",
                "AllowUntrustedServerCertificate",
                true,
                "Allow heartbeat requests to ignore HTTPS certificate validation. Use only for private servers with self-signed certificates.");

            _log.LogInfo("[MilkAntiCheatExpertClient] MACE Init...");
            _heartbeatUrl = ServerEndpointResolver.ResolveHeartbeatUrl(_serverBaseUrl.Value);
            if (string.IsNullOrWhiteSpace(_heartbeatUrl))
                _log.LogError("[MilkAntiCheatExpertClient] 未找到远端服务器地址，请检查 SPT/user/launcher/config.json 的 Url 字段或填写 ServerBaseUrl。");
            else
                _log.LogInfo("[MilkAntiCheatExpertClient] Heartbeat endpoint: " + _heartbeatUrl);

            _sessionId = SessionIdResolver.Resolve();
            if (string.IsNullOrWhiteSpace(_sessionId))
                _log.LogWarning("[MilkAntiCheatExpertClient] 启动参数中未找到 24 位 session id，心跳会被服务端拒绝。");
            else
                _log.LogInfo("[MilkAntiCheatExpertClient] Session id resolved.");

            StartCoroutine(HeartbeatLoop());
        }

        private IEnumerator HeartbeatLoop()
        {
            yield return new UnityEngine.WaitForSeconds(InitialHeartbeatDelaySeconds);

            while (true)
            {
                yield return SendHeartbeat();
                yield return new UnityEngine.WaitForSeconds(Math.Max(5, _heartbeatIntervalSeconds.Value));
            }
        }

        private IEnumerator SendHeartbeat()
        {
            if (string.IsNullOrWhiteSpace(_heartbeatUrl))
                yield break;

            ClientHeartbeatEnvelope envelope;

            try
            {
                envelope = new ClientHeartbeatEnvelope
                {
                    SessionId = _sessionId,
                    ClientGuid = ClientConstants.ClientGuid,
                    ClientVersion = ClientConstants.ClientVersion,
                    TimestampUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nonce = Guid.NewGuid().ToString("N"),
                    Integrity = _integrityReporter.BuildReport(),
                    AntiCheat = _antiCheatScanner.Scan()
                };
            }
            catch (Exception exception)
            {
                _log.LogError("[MilkAntiCheatExpertClient] 心跳检测构建失败: " + exception.Message);
                yield break;
            }

            var shouldCrash = ShouldCrashClient(envelope.AntiCheat);
            if (shouldCrash)
            {
                _log.LogError("[MilkAntiCheatExpertClient] 检测到可疑环境，尽量发送最后一次心跳后崩溃客户端。");
            }

            var payload = HeartbeatPayloadWriter.Write(envelope);
            var body = Encoding.UTF8.GetBytes(payload);

            using (var request = new UnityWebRequest(_heartbeatUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                if (shouldCrash)
                    request.timeout = (int)Math.Ceiling(FinalHeartbeatTimeoutSeconds);

                if (_allowUntrustedServerCertificate.Value)
                    request.certificateHandler = new TrustAllCertificateHandler();

                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("X-EasyChecker-Client", ClientConstants.ClientGuid);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.ConnectionError
                    || request.result == UnityWebRequest.Result.ProtocolError)
                {
                    _log.LogWarning("[MilkAntiCheatExpertClient] 心跳失败: " + request.responseCode + " " + request.error);
                    if (!string.IsNullOrWhiteSpace(request.downloadHandler.text))
                        _log.LogWarning("[MilkAntiCheatExpertClient] 服务端响应: " + request.downloadHandler.text);
                }
            }

            if (shouldCrash)
                CrashClient(envelope.AntiCheat);
        }

        private static bool ShouldCrashClient(ClientAntiCheatReport report)
        {
            if (report == null) return false;

            return report.HasCheatEngineProcess
                   || report.HasSuspiciousKernelModule
                   || report.HasMemoryPatchTool
                   || (report.Findings != null && report.Findings.Count > 0);
        }

        private static void CrashClient(ClientAntiCheatReport report)
        {
            var reason = report == null || report.Findings == null || report.Findings.Count == 0
                ? "Anti-cheat detection triggered."
                : "Anti-cheat detection triggered: " + string.Join(", ", report.Findings.ToArray());

            Environment.FailFast(reason);
        }
    }
}
