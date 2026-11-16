using Newtonsoft.Json;
using QBDL.Web.Models;

namespace QBDL.Web.Services;

public class SettingsService
{
    private readonly string _configPath;
    private AppSettings _settings;
    private readonly object _lock = new();

    public SettingsService()
    {
        _configPath = Path.Combine(
            Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/config",
            "settings.json");

        _settings = Load();
    }

    public AppSettings Settings
    {
        get { lock (_lock) return _settings; }
    }

    /// <summary>Chemin de settings.json. La migration vers les comptes y relit les champs que
    /// <see cref="AppSettings"/> ne déclare plus, et en fait une copie de sauvegarde.</summary>
    public string FilePath => _configPath;

    public void Save()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(_configPath, json);
        }
    }

    public void Update(Action<AppSettings> action)
    {
        lock (_lock)
        {
            action(_settings);
            Save();
        }
    }

    private AppSettings Load()
    {
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
        }
        return new AppSettings();
    }
}
