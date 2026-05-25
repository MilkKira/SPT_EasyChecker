using System;
using System.Text.RegularExpressions;

namespace SPT_EasyChecker_Client
{
    internal static class SessionIdResolver
    {
        private static readonly Regex MongoIdRegex = new Regex(
            "(?<id>[a-fA-F0-9]{24})",
            RegexOptions.Compiled);

        public static string Resolve()
        {
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                var id = ExtractMongoId(argument);
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }

            return string.Empty;
        }

        private static string ExtractMongoId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var match = MongoIdRegex.Match(value);
            return match.Success ? match.Groups["id"].Value.ToLowerInvariant() : string.Empty;
        }
    }
}
