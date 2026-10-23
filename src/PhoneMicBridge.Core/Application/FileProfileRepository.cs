using System;
using System.IO;
using System.Text.Json;
using PhoneMicBridge.Core;

namespace PhoneMicBridge.Application
{
    public class FileProfileRepository : IProfileRepository
    {
        private readonly string _profileFilePath;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public FileProfileRepository(string? customPath = null)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                _profileFilePath = customPath;
            }
            else
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = Path.Combine(appData, "PhoneMicBridge", "profiles");
                _profileFilePath = Path.Combine(dir, "default.json");
            }
        }

        public UserProfileConfig LoadProfile()
        {
            try
            {
                if (!File.Exists(_profileFilePath))
                {
                    return new UserProfileConfig();
                }

                var json = File.ReadAllText(_profileFilePath);
                var config = JsonSerializer.Deserialize<UserProfileConfig>(json, JsonOptions);
                return config ?? new UserProfileConfig();
            }
            catch
            {
                return new UserProfileConfig();
            }
        }

        public void SaveProfile(UserProfileConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(_profileFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(_profileFilePath, json);
            }
            catch
            {
                // Silently fallback without crashing UI/App
            }
        }
    }
}
