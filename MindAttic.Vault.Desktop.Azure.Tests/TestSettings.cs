using MindAttic.Vault.Desktop.Azure.Settings;
using MindAttic.Vault.Desktop.Azure.Tests.Fakes;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Azure.Tests;

public enum TestMode { Off, Slow, Fast }

public sealed class TestInner
{
    public int Depth { get; set; } = 2;
}

public sealed class TestDisplay
{
    public string Theme { get; set; } = "dark";
    public double Scale { get; set; } = 1.5;
    public TestInner Inner { get; set; } = new();
}

public sealed class TestEndpoint
{
    public string Name { get; set; } = "default";
    public int Port { get; set; } = 80;
    public bool Secure { get; set; } = true;
    public List<string> Tags { get; set; } = new();
}

/// <summary>One property of every <see cref="SettingKind"/>.</summary>
[SettingsApp("TestApp", "Test App")]
public sealed class EveryKindSettings
{
    public string Title { get; set; } = "Hello";                                       // Text
    [Multiline] public string Notes { get; set; } = "line1\nline2";                     // Multiline
    public bool Enabled { get; set; } = true;                                          // Bool
    public int Count { get; set; } = 3;                                                // Integer
    public long Big { get; set; } = 9007199254740993;                                  // Integer beyond double precision
    public decimal Price { get; set; } = 10.25m;                                       // Decimal
    public double Ratio { get; set; } = 0.1;                                           // Decimal
    public TestMode Mode { get; set; } = TestMode.Slow;                                // Choice (numeric JSON)
    [SettingChoices("red", "green")] public string Color { get; set; } = "red";        // Choice (string JSON)
    public List<string> Names { get; set; } = new() { "a", "b" };                     // StringList
    [PathPicker(PathKind.Folder)] public string Folder { get; set; } = @"C:\Data";     // Path
    [UrlSetting] public string Site { get; set; } = "https://example.com/a?b=c";      // Url
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);                  // TimeSpan
    public DateTime Start { get; set; } = new(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc); // DateTime
    public TestDisplay Display { get; set; } = new();                                  // Object (nested object inside)

    [SettingCollection(ItemName = "endpoint")]
    public List<TestEndpoint> Endpoints { get; set; } = new()                          // Collection
    {
        new() { Name = "a", Port = 1 },
        new() { Name = "b", Port = 2, Tags = new() { "x" } },
    };

    public Dictionary<string, string> Headers { get; set; } = new() { ["X-One"] = "1" }; // Dictionary
    public List<int> Numbers { get; set; } = new() { 1, 2 };                            // Unknown (typed by its default)
    public int? Optional { get; set; }                                                 // nullable Integer
    public string? Maybe { get; set; }                                                 // nullable Text
}

public static class Harness
{
    public static SettingsManifest Manifest => SettingsManifestBuilder.Build<EveryKindSettings>();

    public static string Name(string property) => $"MindAttic:Settings:TestApp:{property}";

    public static AppConfigurationSettingsBackend Settings(FakeAzure fake) =>
        new(fake.Clients.CreateConfigurationClient(FakeAzure.StoreEndpoint), "teststore");
}
