using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace SPT_EasyChecker_Client
{
    internal static class ServerEndpointResolver
    {
        private const string HeartbeatPath = "/easychecker/heartbeat";
        private static readonly Regex LauncherUrlRegex = new Regex(
            "\"Url\"\\s*:\\s*\"(?<url>(?:\\\\.|[^\"])*)\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ResolveHeartbeatUrl(string configuredServerBaseUrl)
        {
            var launcherUrl = ResolveLauncherConfigUrl();
            if (!string.IsNullOrWhiteSpace(launcherUrl))
                return launcherUrl;

            var configured = NormalizeConfiguredValue(configuredServerBaseUrl);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            foreach (var argument in Environment.GetCommandLineArgs())
            {
                var url = ExtractUrl(argument);
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    return BuildHeartbeatUrl(uri);
                }
            }

            return string.Empty;
        }

        private static string ResolveLauncherConfigUrl()
        {
            foreach (var configPath in EnumerateLauncherConfigCandidates())
            {
                try
                {
                    if (!File.Exists(configPath)) continue;

                    var json = File.ReadAllText(configPath);
                    var match = LauncherUrlRegex.Match(json);
                    if (!match.Success) continue;

                    var rawUrl = UnescapeJsonString(match.Groups["url"].Value);
                    var normalized = NormalizeConfiguredValue(rawUrl);
                    if (!string.IsNullOrWhiteSpace(normalized))
                        return normalized;
                }
                catch
                {
                    // Ignore unreadable candidate paths and try the next likely game root.
                }
            }

            return string.Empty;
        }

        private static string[] EnumerateLauncherConfigCandidates()
        {
            var roots = new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.CurrentDirectory,
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .SelectMany(EnumerateParents)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            return roots
                .Select(root => Path.Combine(root, "SPT", "user", "launcher", "config.json"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string[] EnumerateParents(string startDirectory)
        {
            var directories = new System.Collections.Generic.List<string>();
            var current = new DirectoryInfo(startDirectory);

            while (current != null)
            {
                directories.Add(current.FullName);
                current = current.Parent;
            }

            return directories.ToArray();
        }

        private static string NormalizeConfiguredValue(string configuredServerBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(configuredServerBaseUrl)) return string.Empty;

            var trimmed = configuredServerBaseUrl.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return string.Empty;

            if (uri.AbsolutePath.Equals(HeartbeatPath, StringComparison.OrdinalIgnoreCase))
                return uri.ToString();

            return BuildHeartbeatUrl(uri);
        }

        private static string BuildHeartbeatUrl(Uri uri)
        {
            var builder = new UriBuilder(uri)
            {
                Path = HeartbeatPath,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.ToString();
        }

        private static string ExtractUrl(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument)) return string.Empty;

            var value = argument.Trim().Trim('"');
            var equalsIndex = value.IndexOf('=');
            if (equalsIndex >= 0 && equalsIndex + 1 < value.Length)
                value = value.Substring(equalsIndex + 1).Trim().Trim('"');

            var httpIndex = value.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            var httpsIndex = value.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            var indexes = new[] { httpIndex, httpsIndex }.Where(index => index >= 0).ToArray();
            if (indexes.Length == 0) return string.Empty;

            return value.Substring(indexes.Min()).Trim();
        }

        private static string UnescapeJsonString(string value)
        {
            return value
                .Replace("\\/", "/")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }
    }
}
