using System;
using System.Text.RegularExpressions;

namespace PhoneMicBridge.Application
{
    public class DiagnosticsBundleExporter
    {
        private static readonly Regex MacAddressRegex = new(@"([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})", RegexOptions.Compiled);

        public string Redact(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            // 1. Redact MAC Bluetooth addresses
            var redacted = MacAddressRegex.Replace(input, "[REDACTED_BT_ADDR]");

            // 2. Redact Windows Username and user directories
            var userName = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(userName))
            {
                redacted = Regex.Replace(redacted, Regex.Escape(userName), "[REDACTED_USER]", RegexOptions.IgnoreCase);
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                redacted = Regex.Replace(redacted, Regex.Escape(userProfile), "[REDACTED_USER_PATH]", RegexOptions.IgnoreCase);
            }

            return redacted;
        }

        public string GenerateBundle(string? customEventLogs = null)
        {
            var rawLogs = $@"=== PhoneMicBridge Diagnostics Bundle ===
GeneratedAt: {DateTime.UtcNow:O}
OSVersion: {Environment.OSVersion}
RuntimeVersion: {Environment.Version}
MachineName: {Environment.MachineName}
UserName: {Environment.UserName}
UserProfile: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}
EventLogs:
{customEventLogs ?? "No active event logs recorded."}
=========================================";

            return Redact(rawLogs);
        }
    }
}
