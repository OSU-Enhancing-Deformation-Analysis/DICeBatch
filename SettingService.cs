using System;
using System.IO;
using System.Text.Json;

namespace DICeBatch
{
    public static class SettingsService
    {
        private const string AppFolderName = "DICeBatch";
        private const string SettingsFileName = "settings.json";

        public static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName,
                SettingsFileName
            );

        public static AppSettings Load()
        {
            try
            {
                var path = SettingsPath;
                if (!File.Exists(path))
                    return new AppSettings();

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                // If the file is corrupt or unreadable, don’t crash the app.
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(path, json);
        }
    }
}
