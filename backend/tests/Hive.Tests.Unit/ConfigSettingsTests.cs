using Hive.Infrastructure;

namespace Hive.Tests.Unit;

public class ConfigSettingsTests
{
    private static ConfigDef Def(string key) => ConfigSettings.Find(key)!;

    [Theory]
    [InlineData("timezone", "Europe/Moscow", true)]
    [InlineData("timezone", "Mars/Olympus", false)]
    [InlineData("auth.newdevicecode", "External", true)]
    [InlineData("auth.newdevicecode", "Never", false)]
    [InlineData("sensors.air_warn_c", "47.5", true)]
    [InlineData("sensors.air_warn_c", "hot", false)]
    [InlineData("sensors.disk_warn_pct", "101", false)]
    [InlineData("cluster.notes_in_city", "false", true)]
    [InlineData("cluster.notes_in_city", "yes", false)]
    [InlineData("assistant.baseurl", "https://api.deepseek.com", true)]
    [InlineData("assistant.baseurl", "api.deepseek.com", false)]
    [InlineData("assistant.maxrequestsperday", "0", false)]
    [InlineData("smtp.port", "587", true)]
    [InlineData("smtp.port", "70000", false)]
    [InlineData("backup.at", "03:30", true)]
    [InlineData("backup.at", "25:00", false)]
    [InlineData("backup.at", "3:30", false)]
    [InlineData("backup.verify_day", "0", true)]
    [InlineData("backup.verify_day", "8", false)]
    [InlineData("backup.repository", "/repo/nas", true)]
    [InlineData("backup.repository", "sftp:user@nas:/backups/hive", true)]
    [InlineData("backup.repository", "rest:https://nas.local:8000/hive", true)]
    [InlineData("backup.repository", "nas/backups", false)]
    [InlineData("backup.repository", "/repo/my backups", false)]
    [InlineData("smtp.host", "", true)] // empty clears the setting
    public void Validation_matches_the_kind(string key, string value, bool ok) =>
        Assert.Equal(ok, ConfigSettings.Validate(Def(key), value) is null);

    [Fact]
    public void Keys_and_config_paths_are_unique_and_sections_known()
    {
        Assert.Equal(ConfigSettings.All.Count, ConfigSettings.All.Select(d => d.Key).Distinct().Count());
        Assert.Equal(ConfigSettings.All.Count, ConfigSettings.All.Select(d => d.ConfigPath).Distinct().Count());
        Assert.All(ConfigSettings.All, d => Assert.Contains(d.Section, ConfigSettings.Sections));
        // Every default passes its own validation.
        Assert.All(ConfigSettings.All.Where(d => d.Default.Length > 0), d => Assert.Null(ConfigSettings.Validate(d, d.Default)));
    }
}

public class ConfigSettingsTranslationsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Every_parameter_and_section_has_a_title_and_a_hint_in_the_ui(string language)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "frontend", "src", "i18n", language + ".json")));
        var config = doc.RootElement.GetProperty("config");
        foreach (var def in ConfigSettings.All)
        {
            var p = config.GetProperty("params").GetProperty(def.Key.Replace('.', '_'));
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("name").GetString()), def.Key);
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("hint").GetString()), def.Key);
            foreach (var choice in def.Choices ?? [])
                Assert.False(string.IsNullOrWhiteSpace(config.GetProperty("choices").GetProperty(def.Key.Replace('.', '_')).GetProperty(choice).GetString()), def.Key + "/" + choice);
        }
        foreach (var section in ConfigSettings.Sections)
            Assert.False(string.IsNullOrWhiteSpace(config.GetProperty("sections").GetProperty(section).GetProperty("title").GetString()), section);
    }
}
