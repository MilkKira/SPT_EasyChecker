using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;

namespace SPT_EasyChecker_Client
{
    internal sealed class IntegrityReporter
    {
        public ClientIntegrityReport BuildReport()
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var clientPath = Assembly.GetExecutingAssembly().Location;
            var fikaCorePath = ResolveFikaCorePath();

            AddHash(hashes, ClientConstants.ClientPluginFileName, clientPath);
            AddHash(hashes, ClientConstants.FikaCoreFileName, fikaCorePath);

            return new ClientIntegrityReport
            {
                ClientPluginPresent = !string.IsNullOrWhiteSpace(clientPath) && File.Exists(clientPath),
                ClientPluginPath = clientPath ?? string.Empty,
                FileSha256 = hashes
            };
        }

        private static string ResolveFikaCorePath()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.Equals(assembly.GetName().Name, "Fika.Core", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
                        return assembly.Location;
                }
                catch
                {
                    // Some dynamic assemblies throw when Location is read.
                }
            }

            var executingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            var candidates = new[]
            {
                Path.Combine(executingDirectory, ClientConstants.FikaCoreFileName),
                Path.Combine(Directory.GetParent(executingDirectory)?.FullName ?? executingDirectory, ClientConstants.FikaCoreFileName),
                Path.Combine(baseDirectory, "BepInEx", "plugins", ClientConstants.FikaCoreFileName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return string.Empty;
        }

        private static void AddHash(IDictionary<string, string> hashes, string logicalName, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
            {
                hashes[logicalName] = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
