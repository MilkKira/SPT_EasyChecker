using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SPTarkov.Server.Core.Models.Utils;

namespace SPT_EasyChecker_Server;

internal static class RequestWhitelistStore
{
    private sealed class RequestWhitelistFile
    {
        [JsonPropertyName("preHeartbeat")]
        public RequestWhitelistSection PreHeartbeat { get; set; } = new();
    }

    private sealed class RequestWhitelistSection
    {
        [JsonPropertyName("paths")]
        public List<string> Paths { get; set; } = [];

        [JsonPropertyName("prefixes")]
        public List<string> Prefixes { get; set; } = [];
    }

    private sealed record CompiledWhitelist(
        HashSet<string> Paths,
        string[] Prefixes);

    private const string WhitelistFileName = "request-whitelist.json";

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    private static CompiledWhitelist _preHeartbeat = Compile(DefaultPreHeartbeat());

    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var baseDirectory = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppContext.BaseDirectory
            : assemblyDirectory;

        var whitelistPath = Path.Combine(baseDirectory, WhitelistFileName);
        EnsureWhitelistFile(whitelistPath);
        LoadWhitelist(whitelistPath);
    }

    public static bool IsPreHeartbeatAllowed(HttpContext context)
    {
        return Matches(context, _preHeartbeat);
    }

    private static bool Matches(HttpContext context, CompiledWhitelist whitelist)
    {
        if (!context.Request.Path.HasValue) return false;

        var path = context.Request.Path.Value;
        if (string.IsNullOrWhiteSpace(path)) return false;

        lock (SyncRoot)
        {
            return whitelist.Paths.Contains(path)
                   || whitelist.Prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void LoadWhitelist(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<RequestWhitelistFile>(json, JsonOptions) ?? CreateDefaultFile();

            lock (SyncRoot)
            {
                _preHeartbeat = Compile(file.PreHeartbeat);
            }

            _logger?.Info(
                $"[MilkAntiCheatExpert] 请求白名单已加载: {path}, preHeartbeat(paths={_preHeartbeat.Paths.Count}, prefixes={_preHeartbeat.Prefixes.Length})");
        }
        catch (Exception exception)
        {
            _logger?.Warning($"[MilkAntiCheatExpert] 请求白名单加载失败，使用内置默认值: {exception.Message}");
        }
    }

    private static void EnsureWhitelistFile(string path)
    {
        if (File.Exists(path)) return;

        File.WriteAllText(path, JsonSerializer.Serialize(CreateDefaultFile(), JsonOptions));
    }

    private static CompiledWhitelist Compile(RequestWhitelistSection? section)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new List<string>();

        foreach (var path in section?.Paths ?? [])
        {
            var normalized = NormalizePath(path);
            if (!string.IsNullOrWhiteSpace(normalized))
                paths.Add(normalized);
        }

        foreach (var prefix in section?.Prefixes ?? [])
        {
            var normalized = NormalizePath(prefix);
            if (!string.IsNullOrWhiteSpace(normalized))
                prefixes.Add(normalized);
        }

        return new CompiledWhitelist(paths, prefixes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var trimmed = path.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static RequestWhitelistFile CreateDefaultFile()
    {
        return new RequestWhitelistFile
        {
            PreHeartbeat = DefaultPreHeartbeat()
        };
    }

    private static RequestWhitelistSection DefaultPreHeartbeat()
    {
        return new RequestWhitelistSection
        {
            Paths =
            [
                Constants.HeartbeatPath,
                "/launcher/server/connect",
                "/launcher/ping",
                "/launcher/server/version",
                "/files/launcher/bg.png",
                "/files/launcher/side_bear.png",
                "/launcher/profiles",
                "/launcher/profile/login",
                "/launcher/profile/register",
                "/launcher/profile/get",
                "/launcher/profile/info",
                "/launcher/server/loadedServerMods",
                "/launcher/server/serverModsUsedBy",
                "/launcher/server/serverModsUsedByProfile",
                "/singleplayer/bosstypes",
                "/singleplayer/settings/version",
                "/singleplayer/release",
                "/singleplayer/enableBSGlogging",
                "/singleplayer/moddedTraders",
                "/singleplayer/bundles",
                "/client/menu/locale/en",
                "/client/game/mode",
                "/client/game/start",
                "/singleplayer/clientmods",
                "/fika/natpunchserver/config",
                "/fika/client/config",
                "/sain/namepersonalities",
                "/showMeTheMoney/getPartialRagfairConfig",
                "/dynamicmaps/load"
            ],
            Prefixes =
            [
                "/launcher/",
                "/files/launcher/",
                "/singleplayer/",
                "/client/menu/locale/",
                "/fika/natpunchserver/",
                "/fika/client/",
                "/sain/",
                "/showMeTheMoney/",
                "/wttcommonlib/",
                "/dynamicmaps/"
            ]
        };
    }

}
