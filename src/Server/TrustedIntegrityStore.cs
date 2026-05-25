using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Utils;

namespace SPT_EasyChecker_Server;

internal static partial class TrustedIntegrityStore
{
    private sealed class IntegrityAllowlist
    {
        [JsonPropertyName("files")]
        public Dictionary<string, List<string>> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private const string AllowlistFileName = "integrity-allowlist.json";

    private static readonly object SyncRoot = new();
    private static readonly Regex Sha256Regex = CreateSha256Regex();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    private static Dictionary<string, HashSet<string>> _trustedHashes = new(StringComparer.OrdinalIgnoreCase);
    private static string? _allowlistPath;

    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var baseDirectory = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppContext.BaseDirectory
            : assemblyDirectory;

        _allowlistPath = Path.Combine(baseDirectory, AllowlistFileName);
        EnsureAllowlistFile(_allowlistPath);
        LoadAllowlist(_allowlistPath);
    }

    public static bool HasRules => _trustedHashes.Count > 0;

    public static bool IsTrusted(string fileName, string sha256)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(sha256)) return false;

        lock (SyncRoot)
        {
            return _trustedHashes.TryGetValue(fileName, out var hashes)
                   && hashes.Contains(sha256.Trim());
        }
    }

    private static void LoadAllowlist(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var allowlist = JsonSerializer.Deserialize<IntegrityAllowlist>(json, JsonOptions) ?? new IntegrityAllowlist();
            var trustedHashes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (fileName, hashes) in allowlist.Files)
            {
                var validHashes = hashes
                    .Where(hash => !string.IsNullOrWhiteSpace(hash))
                    .Select(hash => hash.Trim().ToLowerInvariant())
                    .Where(hash => Sha256Regex.IsMatch(hash))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (validHashes.Count > 0)
                    trustedHashes[fileName] = validHashes;
            }

            lock (SyncRoot)
            {
                _trustedHashes = trustedHashes;
            }

            _logger?.Info($"[MilkAntiCheatExpert] 完整性白名单已加载: {path}, 文件规则数: {trustedHashes.Count}");
            if (trustedHashes.Count == 0)
                _logger?.Warning("[MilkAntiCheatExpert] 完整性白名单为空，将只执行心跳内的会话基线校验。");
        }
        catch (Exception exception)
        {
            _logger?.Warning($"[MilkAntiCheatExpert] 完整性白名单加载失败: {exception.Message}");
        }
    }

    private static void EnsureAllowlistFile(string path)
    {
        if (File.Exists(path)) return;

        var template = new IntegrityAllowlist
        {
            Files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [Constants.FikaCoreFileName] = [],
                [Constants.ClientPluginFileName] = []
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(template, JsonOptions));
    }

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex CreateSha256Regex();
}
