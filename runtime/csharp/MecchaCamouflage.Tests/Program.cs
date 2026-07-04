using System.Text.Json;
using MecchaCamouflage.Controller;
using MecchaCamouflage.Core;

var tests = new List<(string Name, Action Run)>
{
    ("legacy false region migrates to fill", LegacyFalseRegionMigratesToFill),
    ("payload includes fill material and region modes", PayloadIncludesFillMaterial),
    ("locales have complete keys", LocalesHaveCompleteKeys),
    ("color parser accepts rrggbb", ColorParserAcceptsHex),
    ("runtime cleanup removes old hash dirs", RuntimeCleanupRemovesOldHashDirs),
    ("auto material defaults off", AutoMaterialDefaultsOff),
    ("front region defaults to fill", FrontRegionDefaultsToFill),
    ("bridge messages are user friendly", BridgeMessagesAreUserFriendly),
    ("settings clamp syncs coverage step to brush size", SettingsClampSyncsCoverageToBrush),
    ("settings clamp limits batch tuning", SettingsClampLimitsBatchTuning),
    ("settings detect supported system language", SettingsDetectSupportedSystemLanguage),
    ("ui snapshot serializes server batch limit", UiSnapshotSerializesServerBatchLimit),
    ("hotkey validation rejects duplicates", HotkeyValidationRejectsDuplicates),
    ("host session reset restores setting default", HostSessionResetRestoresDefault),
    ("host session brush update syncs coverage step", HostSessionBrushUpdateSyncsCoverageStep),
    ("host session rolls back invalid hotkey update", HostSessionRollsBackInvalidHotkeyUpdate),
    ("host session applies multiple setting updates atomically", HostSessionAppliesMultipleSettingUpdatesAtomically),
    ("host session rolls back duplicate hotkey batch", HostSessionRollsBackDuplicateHotkeyBatch),
    ("host session rolls back invalid fill color batch", HostSessionRollsBackInvalidFillColorBatch),
    ("host session rolls back invalid theme color batch", HostSessionRollsBackInvalidThemeColorBatch),
    ("host session rolls back invalid region mode batch", HostSessionRollsBackInvalidRegionModeBatch)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        ++failed;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void LegacyFalseRegionMigratesToFill()
{
    using var temp = new TempHome();
    var paths = new AppPaths("test-version");
    Directory.CreateDirectory(paths.VersionRoot);
    File.WriteAllText(paths.LegacyConfigPath, """
    {
      "layout_version": 23,
      "enable_front_paint": false,
      "enable_side_paint": true,
      "enable_back_paint": false
    }
    """);

    var settings = new SettingsStore(paths).Load();
    Assert(settings.Paint.FrontRegionMode == RegionMode.Fill, "front should migrate to fill");
    Assert(settings.Paint.SideRegionMode == RegionMode.Paint, "side should migrate to paint");
    Assert(settings.Paint.BackRegionMode == RegionMode.Fill, "back should migrate to fill");
}

static void PayloadIncludesFillMaterial()
{
    var settings = new AppSettings();
    settings.Paint.FrontRegionMode = RegionMode.Fill;
    settings.Paint.SideRegionMode = RegionMode.Skip;
    settings.Paint.BackRegionMode = RegionMode.Paint;
    settings.Paint.FillColor = new RgbColor(241, 17, 17);
    settings.Paint.FillMetallic = 1.0;
    settings.Paint.FillRoughness = 0.0;

    var payload = BridgePayloadBuilder.BuildPaintPayload(settings, 42, "Game.exe", new PaintRequestOptions());
    using var doc = JsonDocument.Parse(payload);
    var tuning = doc.RootElement.GetProperty("tuning");
    Assert(tuning.GetProperty("front_region_mode").GetString() == "fill", "front mode missing");
    Assert(tuning.GetProperty("side_region_mode").GetString() == "skip", "side mode missing");
    Assert(tuning.GetProperty("back_region_mode").GetString() == "paint", "back mode missing");
    Assert(tuning.GetProperty("fill_color").GetString() == "#F11111", "fill color missing");
    Assert(Math.Abs(tuning.GetProperty("fill_color_r").GetDouble() - (241.0 / 255.0)) < 0.00001, "fill red not normalized");
    Assert(tuning.GetProperty("enable_front_paint").GetBoolean() == false, "compat front bool wrong");
    Assert(tuning.GetProperty("enable_back_paint").GetBoolean(), "compat back bool wrong");
}

static void LocalesHaveCompleteKeys()
{
    var catalog = LocalizationCatalog.Load();
    var all = catalog.All;
    var englishKeys = all["en"].Keys.Order().ToArray();
    foreach (var locale in LocalizationCatalog.SupportedLocales)
    {
        Assert(all.ContainsKey(locale.Code), $"missing locale {locale.Code}");
        var keys = all[locale.Code].Keys.Order().ToArray();
        Assert(englishKeys.SequenceEqual(keys), $"key mismatch for {locale.Code}");
    }
}

static void ColorParserAcceptsHex()
{
    Assert(RgbColor.TryParse("F11111", out var color), "hex without # should parse");
    Assert(color.ToHex() == "#F11111", "hex roundtrip failed");
}

static void RuntimeCleanupRemovesOldHashDirs()
{
    using var temp = new TempHome();
    var paths = new AppPaths("cleanup-test");
    var keep = Path.Combine(paths.RuntimeBinDirectory, "keep");
    var recent = Path.Combine(paths.RuntimeBinDirectory, "recent");
    var old = Path.Combine(paths.RuntimeBinDirectory, "old");
    Directory.CreateDirectory(keep);
    Directory.CreateDirectory(recent);
    Directory.CreateDirectory(old);
    File.WriteAllText(Path.Combine(keep, "current.dll"), "");
    File.WriteAllText(Path.Combine(recent, "bridge.dll"), "");
    File.WriteAllText(Path.Combine(old, "bridge.dll"), "");
    Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(30));

    paths.CleanupRuntimeBinDirectories(keep, TimeSpan.FromDays(14), keepNewest: 3);

    Assert(Directory.Exists(keep), "current hash dir should be kept");
    Assert(Directory.Exists(recent), "recent hash dir should be kept");
    Assert(!Directory.Exists(old), "old hash dir should be removed");
}

static void AutoMaterialDefaultsOff()
{
    Assert(!new AppSettings().Paint.AutoMaterial, "auto material should default off");
}

static void FrontRegionDefaultsToFill()
{
    Assert(new AppSettings().Paint.FrontRegionMode == RegionMode.Fill, "front should default to fill");
}

static void BridgeMessagesAreUserFriendly()
{
    var alreadyRunning = HostSession.FriendlyBridgeMessage("mesh-first paint is already running");
    var completed = HostSession.FriendlyBridgeMessage("mesh-first paint completed");
    var sent = HostSession.FriendlyBridgeMessage("paint sent through ServerPaintBatch one stroke at a time");
    var preview = HostSession.FriendlyBridgeMessage("local preview material texture imported");

    Assert(alreadyRunning == "Paint is already running.", "already-running message should be friendly");
    Assert(completed == "Paint completed.", "completed message should be friendly");
    Assert(sent == "Paint completed.", "server paint sent message should be friendly");
    Assert(preview == "Preview applied.", "preview message should be friendly");
    Assert(!alreadyRunning.Contains("mesh", StringComparison.OrdinalIgnoreCase), "internal mesh wording should be hidden");
}

static void SettingsClampSyncsCoverageToBrush()
{
    var settings = new AppSettings();
    settings.Paint.StrokeSizeTexels = 7.5;
    settings.Paint.CoverageStepTexels = 2.0;

    var clamped = SettingsStore.Clamp(settings);

    Assert(Math.Abs(clamped.Paint.StrokeSizeTexels - 7.5) < 0.000001, "brush size should be clamped independently");
    Assert(Math.Abs(clamped.Paint.CoverageStepTexels - clamped.Paint.StrokeSizeTexels) < 0.000001, "coverage step should follow brush size");
}

static void SettingsClampLimitsBatchTuning()
{
    var settings = new AppSettings();
    settings.Paint.ServerBatchLimit = 1000;
    settings.Paint.ServerBatchDelayMs = 1;

    var clamped = SettingsStore.Clamp(settings);

    Assert(clamped.Paint.ServerBatchLimit == 50, "batch size should clamp to safe max");
    Assert(clamped.Paint.ServerBatchDelayMs == 150, "batch delay should clamp to safe min");
}

static void SettingsDetectSupportedSystemLanguage()
{
    var previous = System.Globalization.CultureInfo.CurrentUICulture;
    try
    {
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ja-JP");
        var settings = SettingsStore.Clamp(new AppSettings());
        Assert(settings.Language == "ja", "blank language should detect supported UI culture");
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentUICulture = previous;
    }
}

static void UiSnapshotSerializesServerBatchLimit()
{
    var snapshot = new PaintSnapshot(
        6.0,
        6.0,
        150,
        50,
        false,
        0.0,
        1.0,
        "fill",
        "paint",
        "paint",
        "#FFFFFF",
        1.0,
        0.0,
        true);
    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    using var doc = JsonDocument.Parse(json);

    Assert(doc.RootElement.TryGetProperty("serverBatchLimit", out var serverBatchLimit), "snapshot should expose serverBatchLimit for app.js");
    Assert(serverBatchLimit.GetInt32() == 50, "serverBatchLimit should carry the configured value");
    Assert(!doc.RootElement.TryGetProperty("batchSize", out _), "snapshot should not expose renamed batchSize");
}

static void HotkeyValidationRejectsDuplicates()
{
    var hotkeys = new HotkeySet("F1", "F1", "F3", "F4");
    Assert(!hotkeys.TryValidate(out var message), "duplicate hotkeys should be rejected");
    Assert(message.Contains("duplicated", StringComparison.OrdinalIgnoreCase), "duplicate message should explain the problem");

    var invalid = new HotkeySet("A", "F2", "F3", "F4");
    Assert(!invalid.TryValidate(out _), "non-function hotkeys should be rejected");
}

static void HostSessionResetRestoresDefault()
{
    using var temp = new TempHome();
    var session = new HostSession("host-reset-test");

    var update = session.UpdateSetting("paint.brushSizeTexels", JsonSerializer.SerializeToElement(12.0));
    Assert(update.Success, update.Message);
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - 12.0) < 0.000001, "setting should update");

    var reset = session.ResetSetting("paint.brushSizeTexels");
    Assert(reset.Success, reset.Message);
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - new AppSettings().Paint.StrokeSizeTexels) < 0.000001, "setting should reset");
}

static void HostSessionBrushUpdateSyncsCoverageStep()
{
    using var temp = new TempHome();
    var session = new HostSession("host-brush-sync-test");

    var update = session.UpdateSetting("paint.brushSizeTexels", JsonSerializer.SerializeToElement(6.5));

    Assert(update.Success, update.Message);
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - 6.5) < 0.000001, "brush size should update");
    Assert(Math.Abs(session.Settings.Paint.CoverageStepTexels - 6.5) < 0.000001, "coverage step should follow brush size");
}

static void HostSessionRollsBackInvalidHotkeyUpdate()
{
    using var temp = new TempHome();
    var session = new HostSession("host-hotkey-rollback-test");
    var original = session.Settings.PreviewHotkey;

    var update = session.UpdateSetting("app.previewHotkey", JsonSerializer.SerializeToElement(session.Settings.StartHotkey));
    Assert(!update.Success, "duplicate hotkey update should fail");
    Assert(session.Settings.PreviewHotkey == original, "failed hotkey update should roll back in memory");
}

static void HostSessionAppliesMultipleSettingUpdatesAtomically()
{
    using var temp = new TempHome();
    var session = new HostSession("host-batch-valid-test");

    var update = session.UpdateSettings([
        new SettingChange("paint.brushSizeTexels", JsonSerializer.SerializeToElement(12.0)),
        new SettingChange("paint.fillColor", JsonSerializer.SerializeToElement("#112233")),
        new SettingChange("app.processName", JsonSerializer.SerializeToElement("Game.exe"))
    ]);

    Assert(update.Success, update.Message);
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - 12.0) < 0.000001, "brush size should update");
    Assert(session.Settings.Paint.FillColor.ToHex() == "#112233", "fill color should update");
    Assert(session.Settings.GameProcessName == "Game.exe", "process name should update");
}

static void HostSessionRollsBackDuplicateHotkeyBatch()
{
    using var temp = new TempHome();
    var session = new HostSession("host-batch-hotkey-rollback-test");
    var originalBrush = session.Settings.Paint.StrokeSizeTexels;
    var originalPreview = session.Settings.PreviewHotkey;

    var update = session.UpdateSettings([
        new SettingChange("paint.brushSizeTexels", JsonSerializer.SerializeToElement(12.0)),
        new SettingChange("app.previewHotkey", JsonSerializer.SerializeToElement(session.Settings.StartHotkey))
    ]);

    Assert(!update.Success, "duplicate hotkey batch should fail");
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - originalBrush) < 0.000001, "non-hotkey change should roll back");
    Assert(session.Settings.PreviewHotkey == originalPreview, "hotkey change should roll back");
}

static void HostSessionRollsBackInvalidFillColorBatch()
{
    using var temp = new TempHome();
    var session = new HostSession("host-batch-color-rollback-test");
    var originalBrush = session.Settings.Paint.StrokeSizeTexels;
    var originalColor = session.Settings.Paint.FillColor;

    var update = session.UpdateSettings([
        new SettingChange("paint.brushSizeTexels", JsonSerializer.SerializeToElement(12.0)),
        new SettingChange("paint.fillColor", JsonSerializer.SerializeToElement("not-a-color"))
    ]);

    Assert(!update.Success, "invalid color batch should fail");
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - originalBrush) < 0.000001, "brush size should roll back");
    Assert(session.Settings.Paint.FillColor == originalColor, "fill color should roll back");
}

static void HostSessionRollsBackInvalidThemeColorBatch()
{
    using var temp = new TempHome();
    var session = new HostSession("host-batch-theme-rollback-test");
    var originalBrush = session.Settings.Paint.StrokeSizeTexels;
    var originalTheme = session.Settings.ThemeColor;

    var update = session.UpdateSettings([
        new SettingChange("paint.brushSizeTexels", JsonSerializer.SerializeToElement(12.0)),
        new SettingChange("app.themeColor", JsonSerializer.SerializeToElement("not-a-color"))
    ]);

    Assert(!update.Success, "invalid theme color batch should fail");
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - originalBrush) < 0.000001, "brush size should roll back");
    Assert(session.Settings.ThemeColor == originalTheme, "theme color should roll back");
}

static void HostSessionRollsBackInvalidRegionModeBatch()
{
    using var temp = new TempHome();
    var session = new HostSession("host-batch-region-rollback-test");
    var originalBrush = session.Settings.Paint.StrokeSizeTexels;
    var originalMode = session.Settings.Paint.FrontRegionMode;

    var update = session.UpdateSettings([
        new SettingChange("paint.brushSizeTexels", JsonSerializer.SerializeToElement(12.0)),
        new SettingChange("paint.frontRegionMode", JsonSerializer.SerializeToElement("invalid"))
    ]);

    Assert(!update.Success, "invalid region mode batch should fail");
    Assert(Math.Abs(session.Settings.Paint.StrokeSizeTexels - originalBrush) < 0.000001, "brush size should roll back");
    Assert(session.Settings.Paint.FrontRegionMode == originalMode, "region mode should roll back");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class TempHome : IDisposable
{
    private readonly string oldLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string temp = Path.Combine(Path.GetTempPath(), "meccha-tests-" + Guid.NewGuid().ToString("N"));

    public TempHome()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", temp);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALAPPDATA", oldLocalAppData);
        try { Directory.Delete(temp, true); } catch { }
    }
}
