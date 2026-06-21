using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Spt.Services;
using SPTarkov.Server.Core.Models.Utils;

namespace SPT_EasyChecker_Server;

internal static class FikaCrc32Store
{
    private sealed record IntegrityBaseline(
        [property: JsonPropertyName("guid")] string Guid,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("crc32")] string Crc32,
        [property: JsonPropertyName("learnedAt")] DateTimeOffset LearnedAt);

    private const string BaselineFileName = "fika-integrity.json";
    private const uint Crc32Polynomial = 0xEDB88320u;

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    private static string? _baselinePath;
    private static IntegrityBaseline? _baseline;
    private static string? _loadError;

    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;

        var modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(modDirectory))
            modDirectory = AppContext.BaseDirectory;

        _baselinePath = Path.Combine(modDirectory, BaselineFileName);
        LoadBaseline();
    }

    public static bool ValidateOrLearn(ProfileActiveClientMods fikaMod, out string message)
    {
        var current = CreateBaseline(fikaMod);

        lock (SyncRoot)
        {
            if (_loadError is not null)
            {
                message = _loadError;
                return false;
            }

            if (_baseline is not null)
            {
                if (string.Equals(_baseline.Crc32, current.Crc32, StringComparison.Ordinal))
                {
                    message = string.Empty;
                    return true;
                }

                message =
                    $"Fika ChainLoader CRC32 不匹配，期望 {_baseline.Crc32}，实际 {current.Crc32}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_baselinePath))
            {
                message = "Fika CRC32 存储尚未初始化";
                return false;
            }

            try
            {
                SaveBaseline(_baselinePath, current);
                _baseline = current;
                _logger?.Info(
                    $"[MilkAntiCheatExpert] 已学习首个客户端 Fika ChainLoader CRC32: {current.Crc32} "
                    + $"({current.Guid}, {current.Version})");
                message = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                message = $"Fika CRC32 基线保存失败: {exception.Message}";
                _logger?.Warning($"[MilkAntiCheatExpert] {message}");
                return false;
            }
        }
    }

    private static IntegrityBaseline CreateBaseline(ProfileActiveClientMods fikaMod)
    {
        var guid = fikaMod.GUID?.Trim() ?? string.Empty;
        var name = fikaMod.Name?.Trim() ?? string.Empty;
        var version = fikaMod.Version?.ToString() ?? string.Empty;
        var canonicalValue = string.Join("\n", guid.ToLowerInvariant(), name, version);

        return new IntegrityBaseline(
            guid,
            name,
            version,
            ComputeCrc32(canonicalValue),
            DateTimeOffset.UtcNow);
    }

    private static void LoadBaseline()
    {
        lock (SyncRoot)
        {
            _baseline = null;
            _loadError = null;

            if (string.IsNullOrWhiteSpace(_baselinePath) || !File.Exists(_baselinePath))
            {
                _logger?.Info(
                    "[MilkAntiCheatExpert] 尚无 Fika CRC32 基线，将学习首个通过 ChainLoader 校验的客户端。");
                return;
            }

            try
            {
                var json = File.ReadAllText(_baselinePath);
                var baseline = JsonSerializer.Deserialize<IntegrityBaseline>(json, JsonOptions);

                if (baseline is null || !IsValidCrc32(baseline.Crc32))
                    throw new InvalidDataException("JSON 中缺少有效的 CRC32");

                _baseline = baseline with { Crc32 = baseline.Crc32.ToUpperInvariant() };
                _logger?.Info(
                    $"[MilkAntiCheatExpert] 已加载 Fika ChainLoader CRC32 基线: {_baseline.Crc32}");
            }
            catch (Exception exception)
            {
                _loadError = $"Fika CRC32 基线文件无效，请修复或删除 {_baselinePath}: {exception.Message}";
                _logger?.Warning($"[MilkAntiCheatExpert] {_loadError}");
            }
        }
    }

    private static void SaveBaseline(string path, IntegrityBaseline baseline)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(baseline, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private static string ComputeCrc32(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var crc = uint.MaxValue;

        foreach (var valueByte in bytes)
        {
            crc ^= valueByte;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : Crc32Polynomial);
        }

        return (~crc).ToString("X8");
    }

    private static bool IsValidCrc32(string? crc32)
    {
        return crc32?.Length == 8
               && uint.TryParse(
                   crc32,
                   System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out _);
    }
}
