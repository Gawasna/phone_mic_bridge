using System;
using System.IO;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class UserProfileAndDiagnosticsTests : IDisposable
    {
        private readonly string _tempProfileDir;
        private readonly string _tempProfileFile;

        public UserProfileAndDiagnosticsTests()
        {
            _tempProfileDir = Path.Combine(Path.GetTempPath(), "PhoneMicBridge_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempProfileDir);
            _tempProfileFile = Path.Combine(_tempProfileDir, "test_profile.json");
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempProfileDir))
            {
                try { Directory.Delete(_tempProfileDir, true); } catch { }
            }
        }

        [Fact]
        public void ProfileRepository_SavesAndLoads_Accurately()
        {
            var repo = new FileProfileRepository(_tempProfileFile);
            var initialConfig = new UserProfileConfig
            {
                SchemaVersion = 1,
                LastInputDeviceId = "device-mic-123",
                LastTargetDeviceId = "device-spk-456",
                RouteMode = "Out",
                MicGain = 1.4f,
                ToneGain = 0.7f,
                FileGain = 0.9f
            };

            repo.SaveProfile(initialConfig);
            Assert.True(File.Exists(_tempProfileFile));

            var loadedConfig = repo.LoadProfile();
            Assert.NotNull(loadedConfig);
            Assert.Equal(1, loadedConfig.SchemaVersion);
            Assert.Equal("device-mic-123", loadedConfig.LastInputDeviceId);
            Assert.Equal("device-spk-456", loadedConfig.LastTargetDeviceId);
            Assert.Equal("Out", loadedConfig.RouteMode);
            Assert.Equal(1.4f, loadedConfig.MicGain);
            Assert.Equal(0.7f, loadedConfig.ToneGain);
            Assert.Equal(0.9f, loadedConfig.FileGain);
        }

        [Fact]
        public void ProfileRepository_NonExistentFile_ReturnsSafeDefaults()
        {
            var nonExistentPath = Path.Combine(_tempProfileDir, "non_existent.json");
            var repo = new FileProfileRepository(nonExistentPath);

            var fallback = repo.LoadProfile();
            Assert.NotNull(fallback);
            Assert.Equal(1, fallback.SchemaVersion);
            Assert.Null(fallback.LastInputDeviceId);
            Assert.Null(fallback.LastTargetDeviceId);
            Assert.Equal(1.0f, fallback.MicGain);
        }

        [Fact]
        public void DiagnosticsBundleExporter_RedactsMacAndUserInformation()
        {
            var exporter = new DiagnosticsBundleExporter();
            var rawLog = $"Connected to BT adapter 00:1A:7D:DA:71:13 and 00-14-22-01-23-45 for user {Environment.UserName} at {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";

            var redacted = exporter.Redact(rawLog);

            Assert.DoesNotContain("00:1A:7D:DA:71:13", redacted);
            Assert.DoesNotContain("00-14-22-01-23-45", redacted);
            Assert.Contains("[REDACTED_BT_ADDR]", redacted);

            if (!string.IsNullOrWhiteSpace(Environment.UserName))
            {
                Assert.DoesNotContain(Environment.UserName, redacted);
                Assert.Contains("[REDACTED_USER]", redacted);
            }
        }

        [Fact]
        public void DiagnosticsBundleExporter_GenerateBundle_ProducesValidRedactedDocument()
        {
            var exporter = new DiagnosticsBundleExporter();
            var bundle = exporter.GenerateBundle("Test Event: Device 11:22:33:44:55:66 unplugged");

            Assert.Contains("=== PhoneMicBridge Diagnostics Bundle ===", bundle);
            Assert.DoesNotContain("11:22:33:44:55:66", bundle);
            Assert.Contains("[REDACTED_BT_ADDR]", bundle);
        }
    }
}
