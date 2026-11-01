using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class HotkeyDefinitionTests
{
    [Fact]
    public void ANewConfigurationStartsWithTheTwoDefaultHotkeysAndAnEmptySavedListStaysEmpty()
    {
        var hotkeys = AppConfigRules.Normalize(new AppConfig()).Value.Hotkeys;

        Assert.Equal(["optiscaler", "lossless"], hotkeys.Select(hotkey => hotkey.Id));
        Assert.Equal([HotkeyKeys.Insert], hotkeys[0].Keys);
        Assert.Equal([HotkeyKeys.Control, HotkeyKeys.Alt, HotkeyKeys.S], hotkeys[1].Keys);

        var json = JsonSerializer.Serialize(new AppConfig { Hotkeys = [] }, ConfigJsonContext.Default.AppConfig);
        Assert.Empty(ConfigRepair.Deserialize(json)!.Hotkeys);
    }

    [Fact]
    public void NormalizationDropsEntriesWithoutKeysAndInvalidKeysAndRenumbersRepeatedIds()
    {
        var config = new AppConfig
        {
            Hotkeys =
            [
                new HotkeyDefinition { Id = "menu", Name = " OptiScaler menu ", Keys = [HotkeyKeys.Insert] },
                new HotkeyDefinition { Id = "empty", Name = "Nothing", Keys = [] },
                new HotkeyDefinition { Id = "invalid", Name = "Invalid", Keys = [0, 0x1FF, -4] },
                new HotkeyDefinition
                {
                    Id = "menu", Name = "", Keys = [HotkeyKeys.Control, 0x300, HotkeyKeys.Control, HotkeyKeys.S],
                    PressDelayMs = 9000
                },
                new HotkeyDefinition { Id = "", Name = "Enter", Keys = [HotkeyKeys.NumpadEnter], PressDelayMs = -5 }
            ]
        };

        var hotkeys = AppConfigRules.Normalize(config).Value.Hotkeys;

        Assert.Equal(3, hotkeys.Count);
        Assert.Equal("menu", hotkeys[0].Id);
        Assert.Equal("OptiScaler menu", hotkeys[0].Name);
        Assert.NotEqual("menu", hotkeys[1].Id);
        Assert.Equal("Hotkey", hotkeys[1].Name);
        Assert.Equal([HotkeyKeys.Control, HotkeyKeys.S], hotkeys[1].Keys);
        Assert.Equal(HotkeyDefinition.MaximumPressDelayMs, hotkeys[1].PressDelayMs);
        Assert.False(string.IsNullOrWhiteSpace(hotkeys[2].Id));
        Assert.Equal([HotkeyKeys.NumpadEnter], hotkeys[2].Keys);
        Assert.Equal(0, hotkeys[2].PressDelayMs);
        Assert.Equal(3, hotkeys.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void AMissingListNormalizesToEmpty()
    {
        var config = new AppConfig { Hotkeys = null! };

        Assert.Empty(AppConfigRules.Normalize(config).Value.Hotkeys);
    }

    [Fact]
    public void ModifierSwitchesKeepTheirOrderAheadOfTheKey()
    {
        List<int> keys = [HotkeyKeys.S];

        keys = HotkeyKeys.WithModifier(keys, HotkeyKeys.Alt, true);
        keys = HotkeyKeys.WithModifier(keys, HotkeyKeys.Control, true);
        Assert.Equal("Ctrl+Alt+S", HotkeyKeys.Describe(keys));

        keys = HotkeyKeys.WithKey(keys, HotkeyKeys.Insert);
        Assert.Equal([HotkeyKeys.Control, HotkeyKeys.Alt, HotkeyKeys.Insert], keys);

        keys = HotkeyKeys.WithModifier(keys, HotkeyKeys.Control, false);
        Assert.Equal("Alt+Insert", HotkeyKeys.Describe(keys));
    }

    [Fact]
    public void EveryPickerKeyIsSendableAndNamedOnce()
    {
        var keys = HotkeyKeys.Groups.SelectMany(group => group.Keys).ToArray();

        Assert.All(keys, key => Assert.True(HotkeyKeys.IsValid(key.Key)));
        Assert.Equal(keys.Length, keys.Select(key => key.Key).Distinct().Count());
        Assert.Equal("F24", HotkeyKeys.Label(0x87));
        Assert.Equal("Key FE", HotkeyKeys.Label(0xFE));
    }
}
