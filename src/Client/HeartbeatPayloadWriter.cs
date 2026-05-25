using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SPT_EasyChecker_Client
{
    internal static class HeartbeatPayloadWriter
    {
        public static string Write(ClientHeartbeatEnvelope envelope)
        {
            var builder = new StringBuilder(1024);
            builder.Append('{');
            WriteProperty(builder, "sessionId", envelope.SessionId);
            builder.Append(',');
            WriteProperty(builder, "clientGuid", envelope.ClientGuid);
            builder.Append(',');
            WriteProperty(builder, "clientVersion", envelope.ClientVersion);
            builder.Append(',');
            builder.Append("\"timestampUnixSeconds\":").Append(envelope.TimestampUnixSeconds.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            WriteProperty(builder, "nonce", envelope.Nonce);
            builder.Append(',');
            builder.Append("\"integrity\":");
            WriteIntegrity(builder, envelope.Integrity);
            builder.Append(',');
            builder.Append("\"antiCheat\":");
            WriteAntiCheat(builder, envelope.AntiCheat);
            builder.Append('}');
            return builder.ToString();
        }

        private static void WriteIntegrity(StringBuilder builder, ClientIntegrityReport report)
        {
            builder.Append('{');
            builder.Append("\"clientPluginPresent\":").Append(report.ClientPluginPresent ? "true" : "false");
            builder.Append(',');
            WriteProperty(builder, "clientPluginPath", report.ClientPluginPath);
            builder.Append(',');
            builder.Append("\"fileSha256\":");
            WriteDictionary(builder, report.FileSha256);
            builder.Append('}');
        }

        private static void WriteAntiCheat(StringBuilder builder, ClientAntiCheatReport report)
        {
            builder.Append('{');
            builder.Append("\"hasCheatEngineProcess\":").Append(report.HasCheatEngineProcess ? "true" : "false");
            builder.Append(',');
            builder.Append("\"hasSuspiciousKernelModule\":").Append(report.HasSuspiciousKernelModule ? "true" : "false");
            builder.Append(',');
            builder.Append("\"hasMemoryPatchTool\":").Append(report.HasMemoryPatchTool ? "true" : "false");
            builder.Append(',');
            builder.Append("\"findings\":");
            WriteArray(builder, report.Findings);
            builder.Append('}');
        }

        private static void WriteDictionary(StringBuilder builder, IDictionary<string, string> values)
        {
            builder.Append('{');
            var first = true;
            foreach (var pair in values)
            {
                if (!first) builder.Append(',');
                WriteString(builder, pair.Key);
                builder.Append(':');
                WriteString(builder, pair.Value);
                first = false;
            }
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable<string> values)
        {
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first) builder.Append(',');
                WriteString(builder, value);
                first = false;
            }
            builder.Append(']');
        }

        private static void WriteProperty(StringBuilder builder, string name, string value)
        {
            WriteString(builder, name);
            builder.Append(':');
            WriteString(builder, value ?? string.Empty);
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
