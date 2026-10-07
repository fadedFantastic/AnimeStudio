using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

internal sealed class ExplorerSettings
{
    public string WorkDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio", "AssetExplorer");
    public string Catalog = "", SourceRoot = "", CabMap = "", DictionaryDirectory = "", ExportDirectory = "";
    public GameType Game = GameType.ZZZ;
    public int ExportWorkers = 2;
    public bool Full;
    public string ResourceDirectory = "";
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio", "AssetExplorer", "settings.json");
    public static ExplorerSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            try { return JsonConvert.DeserializeObject<ExplorerSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
            catch (JsonException) { }
        }
        var settings = new ExplorerSettings();
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio", "ZzzMap", "gui-settings.json");
        if (File.Exists(legacy))
        {
            var old = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(legacy));
            settings.Catalog = (string)old["LastMap"] ?? "";
            var game = (string)old["GameDir"];
            if (!string.IsNullOrWhiteSpace(game)) settings.SourceRoot = Directory.Exists(Path.Combine(game, "Blocks")) ? Path.Combine(game, "Blocks") : game;
            settings.DictionaryDirectory = Path.Combine((string)old["WorkDir"] ?? Path.GetDirectoryName(legacy), "Maps");
            settings.CabMap = FindCabMap(settings.Catalog);
        }
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        File.WriteAllText(SettingsPath + ".tmp", JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
    public static string FindCabMap(string catalog)
    {
        if (string.IsNullOrEmpty(catalog)) return "";
        var dir = Path.GetDirectoryName(Path.GetFullPath(catalog));
        foreach (var candidate in new[] { Path.ChangeExtension(catalog, ".bin"),
            Path.Combine(dir, "Maps", Path.GetFileNameWithoutExtension(catalog) + ".bin"),
            Path.Combine(dir, "..", "Maps", Path.GetFileNameWithoutExtension(catalog) + ".bin") })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return "";
    }
}
