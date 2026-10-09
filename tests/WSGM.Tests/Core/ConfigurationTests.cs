using System.Text.Json;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Input;
using WSGM.Plugin.Sdk;
using WSGM.Testing;
using WSGM.Tests.Builders;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Core;

public sealed class ConfigurationTests
{
    [Fact]
    public void Normalize_DropsMalformedGameModeAudioPreferenceWithoutChangingTheLaunchConfiguration()
    {
        var config = new AppConfig
        {
            GameModeLaunch = new GameModeLaunchConfiguration
            {
                Kind = GameModeLaunchKind.Custom,
                GameAudio = new AudioProfilePreference
                {
                    Output = new AudioEndpointPreference { Id = "   " },
                    PlaybackFormat = new AudioFormatPreference
                    {
                        Channels = 33,
                        SampleRate = 48000,
                        BitsPerSample = 24,
                        ContainerBitsPerSample = 32,
                        ChannelMask = 0x63F
                    }
                }
            }
        };

        var normalized = AppConfigRules.Normalize(config).Value;

        Assert.Equal(GameModeLaunchKind.Custom, normalized.GameModeLaunch.Kind);
        Assert.Null(normalized.GameModeLaunch.GameAudio);
    }

    [Fact]
    public void Normalize_PreservesValidPendingReturnAudio()
    {
        var config = new AppConfig
        {
            GameModeLaunchRecovery = new GameModeLaunchRecovery
            {
                PendingReturnAudio = new AudioProfilePreference
                {
                    Output = new AudioEndpointPreference { Id = "desktop-output", Name = "Desk speakers" },
                    VolumePercent = 42,
                    Muted = false,
                    PlaybackFormat = new AudioFormatPreference
                    {
                        Channels = 6,
                        SampleRate = 48000,
                        BitsPerSample = 24,
                        ContainerBitsPerSample = 32,
                        ChannelMask = 0x3F
                    },
                    SpatialFormat = CoreAudio.SpatialAudioFormats.Off
                }
            }
        };

        var normalized = AppConfigRules.Normalize(config).Value;

        var audio = Assert.IsType<AudioProfilePreference>(normalized.GameModeLaunchRecovery.PendingReturnAudio);
        Assert.Equal("desktop-output", audio.Output!.Id);
        Assert.Equal(6, audio.PlaybackFormat!.Channels);
        Assert.Equal(CoreAudio.SpatialAudioFormats.Off, audio.SpatialFormat);
    }

    [Fact]
    public void JsonNullIsRejectedInsteadOfBecomingSilentDefaults()
    {
        Assert.Throws<JsonException>(() => ConfigRepair.Deserialize("null"));
    }

    [Fact]
    public void AnUnknownDeviceEnumNameIsRepairedInsteadOfDiscardingTheWholeFile()
    {
        // Every enum here is written by name, so an unrecognised one throws before Normalize can
        // apply its Enum.IsDefined fallbacks. If the repair pass does not cover it, the retry
        // throws too and Load moves the entire file aside — taking the registry recovery snapshots
        // and every unrelated setting with it. The values below are what a hand edit, or a
        // configuration written by a build that knows more names, looks like.
        const string json = """
                            {
                              "AccentColor": "#FF00AA",
                              "DeviceIntegration": {
                                "Enabled": true,
                                "GlyphSelection": "SomethingElse",
                                "DiagnosticLevel": "Verbose",
                                "OemAssignments": [ { "ControlId": "oem1", "Action": "LaunchAnything" } ]
                              },
                              "Profiles": {
                                "Global": {
                                  "ControllerTarget": "NintendoSwitchPro",
                                  "Device": [
                                    {
                                      "DeviceIdentityKey": "device",
                                      "CapabilityId": "power.primary-limit",
                                      "Value": { "Kind": "Wattage", "IntegerValue": 15 }
                                    }
                                  ]
                                },
                                "Games": [ { "Id": "steam:70", "Values": { "ControllerTarget": "NotATarget" } } ]
                              }
                            }
                            """;

        var config = ConfigRepair.Deserialize(json);

        Assert.NotNull(config);
        // The unrelated setting survived, which is the point of repairing rather than discarding.
        Assert.Equal("#FF00AA", config.AccentColor);
        Assert.True(config.DeviceIntegration.Enabled);
        Assert.Null(config.Profiles.Global.ControllerTarget);
        Assert.Equal(DeviceGlyphSelection.Automatic, config.DeviceIntegration.GlyphSelection);
        Assert.Null(Assert.Single(config.Profiles.Games).Values.ControllerTarget);
        Assert.Equal(OemAction.Disabled, Assert.Single(config.DeviceIntegration.OemAssignments).Action);
        Assert.Empty(config.DeviceIntegration.DeviceProfiles);
    }

    [Fact]
    public void UnknownOptionalControllerTargetNumbersRemainUnsetAtEveryProfileLayer()
    {
        const string json = """
                            {
                              "AccentColor": "#FF00AA",
                              "Profiles": {
                                "Global": { "ControllerTarget": 99 },
                                "Games": [ { "Id": "steam:70", "Values": { "ControllerTarget": 99 } } ]
                              }
                            }
                            """;

        var config = AppConfigRules.Normalize(ConfigRepair.Deserialize(json)).Value;
        var restored = ConfigRepair.Deserialize(JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));

        Assert.Equal("#FF00AA", restored.AccentColor);
        Assert.Null(restored.Profiles.Global.ControllerTarget);
        Assert.Null(Assert.Single(restored.Profiles.Games).Values.ControllerTarget);
    }

    [Fact]
    public void UnknownPerformanceAndCachedDeclarationEnumsDoNotDiscardTheConfig()
    {
        const string json = """
                            {
                              "AccentColor": "#FF00AA",
                              "Performance": { "FrameLimitStrategy": "FutureStrategy" },
                              "DeviceIntegration": {
                                "PluginSettings": [
                                  {
                                    "DeviceDefinitionId": "device",
                                    "PluginId": "plugin",
                                    "Declaration": {
                                      "Sections": [ { "SectionId": "general", "Key": "FutureSection" } ],
                                      "Settings": [
                                        {
                                          "SettingId": "future",
                                          "ValueKind": "FutureValue",
                                          "Display": { "Key": "FutureLabel" },
                                          "Default": { "Kind": "FutureValue" },
                                          "Unit": "FutureUnit"
                                        }
                                      ]
                                    }
                                  }
                                ]
                              }
                            }
                            """;

        var config = ConfigRepair.Deserialize(json);

        Assert.NotNull(config);
        Assert.Equal("#FF00AA", config.AccentColor);
        Assert.Equal(FrameLimitStrategy.FrameLimitOnly, config.Performance.FrameLimitStrategy);
        Assert.Empty(config.DeviceIntegration.DeviceProfiles);
    }

    [Fact]
    public void NormalizeDropsAnExecutableClaimedByASecondGameProfile()
    {
        // One executable activates one profile. Two claims make the match ambiguous, and an ambiguous
        // match resolves to no profile at all, so the second claim is dropped.
        var config = new AppConfig
        {
            Profiles = new ProfileConfig
            {
                Games =
                [
                    new GameProfile { Id = "profile:a", ProcessNames = ["game.exe"] },
                    new GameProfile { Id = "profile:b", ProcessNames = ["GAME.EXE", "other.exe"] },
                    new GameProfile { Id = "profile:a" }
                ]
            }
        };

        var games = AppConfigRules.Normalize(config).Value.Profiles.Games;

        Assert.Equal(["profile:a", "profile:b"], games.Select(game => game.Id));
        Assert.Equal(["game.exe"], games[0].ProcessNames);
        Assert.Equal(["other.exe"], games[1].ProcessNames);
    }

    [Fact]
    public void NormalizeRepairsEveryNullableCollectionAndNestedSection()
    {
        var config = new AppConfig
        {
            StartupApps = null!,
            Hotkey = null!,
            GamepadChord = null!,
            Gestures = null!,
            SavedDisplayScaleEntries = null!,
            GameModeLaunch = null!,
            GameModeLaunchRecovery = null!,
            PreviousConsoleLockSchemeValues = null!,
            CardLibraries = null!,
            ForgottenInsertedCardIds = null!,
            CustomTabs = null!,
            LaunchWrappers = null!,
            LibraryTabOrder = null!,
            HiddenNativeTabs = null!,
            KnownNativeTabs = null!,
            AccentColor = null!,
            Splash = null!
        };

        var normalized = AppConfigRules.Normalize(config).Value;

        Assert.NotNull(normalized.StartupApps);
        Assert.NotNull(normalized.Hotkey);
        Assert.NotNull(normalized.GamepadChord);
        Assert.NotNull(normalized.Gestures);
        Assert.NotNull(normalized.SavedDisplayScaleEntries);
        Assert.NotNull(normalized.GameModeLaunch);
        Assert.NotNull(normalized.GameModeLaunchRecovery);
        Assert.NotNull(normalized.PreviousConsoleLockSchemeValues);
        Assert.NotNull(normalized.CardLibraries);
        Assert.NotNull(normalized.ForgottenInsertedCardIds);
        Assert.NotNull(normalized.CustomTabs);
        Assert.NotNull(normalized.LaunchWrappers);
        Assert.NotNull(normalized.LibraryTabOrder);
        Assert.NotNull(normalized.HiddenNativeTabs);
        Assert.NotNull(normalized.KnownNativeTabs);
        Assert.Equal("#FFFF9D3D", normalized.AccentColor);
        Assert.NotNull(normalized.Splash);
    }

    /// A null ELEMENT ("StartupApps": [null]) survives the list-level null repair. It
    /// used to NRE in SelfElevation BEFORE the crash-loop breaker records a start, so
    /// the shell died at every sign-in with nothing left to disarm the boot.
    [Fact]
    public void NormalizeDropsNullElementsFromEveryListAndRepairsTheirStrings()
    {
        var config = new AppConfig
        {
            StartupApps = [null!, new StartupAppConfig { Path = null!, Args = null! }],
            SavedDisplayScaleEntries = [null!, new DisplayScaleEntry { DeviceName = null! }],
            PreviousConsoleLockSchemeValues = [null!, new PowerSchemeConsoleLock { SchemeGuid = null! }]
        };

        var normalized = AppConfigRules.Normalize(config).Value;

        var app = Assert.Single(normalized.StartupApps);
        Assert.Equal("", app.Path);
        Assert.Equal("", app.Args);
        Assert.Equal("", Assert.Single(normalized.SavedDisplayScaleEntries).DeviceName);
        Assert.Equal("", Assert.Single(normalized.PreviousConsoleLockSchemeValues).SchemeGuid);
    }

    /// The resolver takes the first entry matching a key, so a duplicate would make file order
    /// decide which value the device gets.
    [Fact]
    public void NormalizeCollapsesDuplicateDeviceValuesOntoTheFirstAndMasksColours()
    {
        var config = new AppConfig();
        config.DeviceIntegration.PreferencesSchemaVersion = DeviceIntegrationConfig.CurrentPreferencesSchemaVersion;
        config.Profiles.Global.Device =
        [
            new ProfileDeviceValue
            {
                DeviceIdentityKey = "claw", CapabilityId = " fan.mode ",
                Value = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 1 }
            },
            new ProfileDeviceValue
            {
                DeviceIdentityKey = "claw", CapabilityId = "fan.mode",
                Value = new CapabilityValue { Kind = CapabilityValueKind.Integer, IntegerValue = 2 }
            },
            new ProfileDeviceValue
            {
                DeviceIdentityKey = "claw", CapabilityId = "lighting.zone-color", InstanceId = "left-ring",
                Value = new CapabilityValue
                    { Kind = CapabilityValueKind.Color, ColorValue = unchecked((int)0xFF123456) }
            },
            new ProfileDeviceValue { DeviceIdentityKey = "claw", CapabilityId = "empty" }
        ];

        var values = AppConfigRules.Normalize(config).Value.Profiles.Global.Device;

        Assert.Equal(2, values.Count);
        Assert.Equal(1, values[0].Value!.IntegerValue);
        Assert.Equal(0x123456, values[1].Value!.ColorValue);
    }

    [Fact]
    public void AFanCurveReferenceToADeletedProfileFallsBackInsteadOfNamingNothing()
    {
        var config = new AppConfig();
        config.DeviceIntegration.PreferencesSchemaVersion = DeviceIntegrationConfig.CurrentPreferencesSchemaVersion;
        config.DeviceIntegration.DeviceProfiles.Add(new DeviceProfileScope
        {
            DeviceDefinitionId = "device", FamilyId = "family",
            Profiles = [new DeviceAuthoredProfile { ProfileId = "quiet", CapabilityId = "fan.curve" }]
        });
        config.Profiles.Global.FanCurveProfileId = "quiet";
        config.Profiles.Games.Add(new GameProfile { Id = "steam:1", Values = { FanCurveProfileId = "deleted" } });

        var normalized = AppConfigRules.Normalize(config).Value;

        Assert.Equal("quiet", normalized.Profiles.Global.FanCurveProfileId);
        Assert.Null(normalized.Profiles.Games[0].Values.FanCurveProfileId);
    }

    [Fact]
    public void NormalizeRepairsExplicitNullsInsideAnExistingSplashSection()
    {
        var config = new AppConfig
        {
            Splash = new SplashConfig
            {
                Text = null!,
                TextColor = null!,
                Caption = null!,
                CaptionColor = null!,
                SpinnerColor = null!,
                BackgroundColor = null!,
                BackgroundImagePath = null!,
                LogoImagePath = null!,
                TextPlacement = null!,
                SpinnerPlacement = null!,
                LogoPlacement = null!
            }
        };

        var splash = AppConfigRules.Normalize(config).Value.Splash;

        Assert.Equal("Please wait", splash.Text);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal("", splash.Caption);
        Assert.Equal("#666666", splash.CaptionColor);
        Assert.Equal("#FFFFFF", splash.SpinnerColor);
        Assert.Equal("#000000", splash.BackgroundColor);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.Equal("", splash.LogoImagePath);
        Assert.NotNull(splash.TextPlacement);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.NotNull(splash.SpinnerPlacement);
        Assert.Equal(SplashPlacementMode.WithText, splash.SpinnerPlacement.Mode);
        Assert.NotNull(splash.LogoPlacement);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void NormalizeRepairsInvalidPersistedFilterEnums()
    {
        var tab = new CustomTabConfig
        {
            FilterTree = new FilterNode
            {
                Kind = (FilterKind)999,
                Mode = (FilterMode)999,
                CardScope = (SdCardScope)999
            }
        };

        var normalized = AppConfigRules.Normalize(new AppConfig { CustomTabs = [tab] }).Value;

        Assert.Equal(FilterKind.Installed, normalized.CustomTabs[0].FilterTree.Kind);
        Assert.Equal(FilterMode.And, normalized.CustomTabs[0].FilterTree.Mode);
        Assert.Equal(SdCardScope.Inserted, normalized.CustomTabs[0].FilterTree.CardScope);
    }

    [Fact]
    public void NormalizeSplashClampsEveryNumericFieldIntoTheAppearanceEditorBounds()
    {
        // What a shared .wsgmsplash theme or a hand-edited config.json can carry and
        // the Appearance editor's NumericUpDowns can not.
        var splash = new SplashConfig
        {
            TitleFontSize = int.MaxValue,
            CaptionFontSize = 0,
            SpinnerSize = int.MaxValue,
            LogoMaxSize = -12,
            TextPlacement = new SplashElementPlacement
            {
                PaddingX = int.MaxValue,
                PaddingY = int.MinValue,
                X = -5,
                Y = int.MaxValue
            },
            SpinnerPlacement = new SplashElementPlacement { PaddingX = 8192, PaddingY = -1, X = 40000, Y = -40000 },
            LogoPlacement = new SplashElementPlacement { PaddingX = -3, PaddingY = 100000, X = int.MinValue, Y = 20000 }
        };

        SplashRules.Normalize(splash);

        Assert.Equal(400, splash.TitleFontSize);
        Assert.Equal(1, splash.CaptionFontSize);
        Assert.Equal(1024, splash.SpinnerSize);
        Assert.Equal(1, splash.LogoMaxSize);
        Assert.Equal(4096, splash.TextPlacement.PaddingX);
        Assert.Equal(0, splash.TextPlacement.PaddingY);
        Assert.Equal(0, splash.TextPlacement.X);
        Assert.Equal(16384, splash.TextPlacement.Y);
        Assert.Equal(4096, splash.SpinnerPlacement.PaddingX);
        Assert.Equal(0, splash.SpinnerPlacement.PaddingY);
        Assert.Equal(16384, splash.SpinnerPlacement.X);
        Assert.Equal(0, splash.SpinnerPlacement.Y);
        Assert.Equal(0, splash.LogoPlacement.PaddingX);
        Assert.Equal(4096, splash.LogoPlacement.PaddingY);
        Assert.Equal(0, splash.LogoPlacement.X);
        Assert.Equal(16384, splash.LogoPlacement.Y);
    }

    [Fact]
    public void NormalizeSplashLeavesInRangeNumbersAndKnownEnumsUntouched()
    {
        var splash = new SplashConfig
        {
            TitleFontSize = 26,
            CaptionFontSize = 12,
            SpinnerSize = 36,
            LogoMaxSize = 200,
            SpinnerStyle = SplashSpinnerStyle.LiWave,
            SweepEdge = SweepEdge.Top,
            TextPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Absolute,
                Anchor = SplashPlacementAnchor.BottomRight,
                PaddingX = 64,
                PaddingY = 4096,
                X = 0,
                Y = 16384
            }
        };

        SplashRules.Normalize(splash);

        Assert.Equal(26, splash.TitleFontSize);
        Assert.Equal(12, splash.CaptionFontSize);
        Assert.Equal(36, splash.SpinnerSize);
        Assert.Equal(200, splash.LogoMaxSize);
        Assert.Equal(SplashSpinnerStyle.LiWave, splash.SpinnerStyle);
        Assert.Equal(SweepEdge.Top, splash.SweepEdge);
        Assert.Equal(SplashPlacementMode.Absolute, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.BottomRight, splash.TextPlacement.Anchor);
        Assert.Equal(64, splash.TextPlacement.PaddingX);
        Assert.Equal(4096, splash.TextPlacement.PaddingY);
        Assert.Equal(0, splash.TextPlacement.X);
        Assert.Equal(16384, splash.TextPlacement.Y);
    }

    [Fact]
    public void NormalizeSplashDropsEnumMembersThatDoNotExistBackToTheirDefaults()
    {
        // "SpinnerStyle": 999 in the JSON deserializes unchecked into the enum.
        var splash = new SplashConfig
        {
            SpinnerStyle = (SplashSpinnerStyle)999,
            SweepEdge = (SweepEdge)(-4),
            TextPlacement = new SplashElementPlacement
            {
                Mode = (SplashPlacementMode)42,
                Anchor = (SplashPlacementAnchor)(-1)
            }
        };

        SplashRules.Normalize(splash);

        Assert.Equal(SplashSpinnerStyle.Ring, splash.SpinnerStyle);
        Assert.Equal(SweepEdge.Bottom, splash.SweepEdge);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, splash.TextPlacement.Anchor);
    }

    [Fact]
    public void NormalizeSplashTurnsWhitespaceOnlyImagePathsIntoTheSingleNoImageValue()
    {
        // Every consumer reads these with IsNullOrWhiteSpace, so "   " already MEANS
        // no image — persisting it verbatim (config.json, an exported theme) only
        // spreads a second spelling of the same state that nothing can act on.
        var splash = new SplashConfig { LogoImagePath = "   ", BackgroundImagePath = "\t\n" };

        SplashRules.Normalize(splash);

        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal("", splash.BackgroundImagePath);
    }

    [Fact]
    public void NormalizeSplashKeepsRealImagePathsExactlyAsTheyAre()
    {
        // Leading and trailing spaces are legal inside Windows path components, so
        // nothing but the all-whitespace case may be rewritten.
        var splash = new SplashConfig
        {
            LogoImagePath = @"C:\pictures\ spaced logo .png",
            BackgroundImagePath = @"\\server\share\bg.jpg"
        };

        SplashRules.Normalize(splash);

        Assert.Equal(@"C:\pictures\ spaced logo .png", splash.LogoImagePath);
        Assert.Equal(@"\\server\share\bg.jpg", splash.BackgroundImagePath);
    }

    [Fact]
    public void NormalizeSplashKeepsLongDisplayStringsWhole()
    {
        // No length cuts a splash line: what the user or a shared theme wrote is what shows.
        var splash = new SplashConfig
        {
            Text = new string('A', 1_000),
            Caption = new string('B', 1_000),
            TextColor = "#" + new string('F', 50)
        };

        SplashRules.Normalize(splash);

        Assert.Equal(new string('A', 1_000), splash.Text);
        Assert.Equal(new string('B', 1_000), splash.Caption);
        Assert.Equal("#" + new string('F', 50), splash.TextColor);
    }

    [Fact]
    public void NormalizeSplashLeavesOrdinarySplashTextAndColorsExactlyAsTheyAre()
    {
        // Normalizing only fills in what is missing; a real splash line and its colors stay as written.
        var splash = new SplashConfig
        {
            Text = "Starting Steam Big Picture…",
            Caption = "Please wait while the handheld finishes waking up — this takes a moment",
            TextColor = "#FFFFFF",
            CaptionColor = "#80FF9D3D",
            SpinnerColor = "LightGoldenrodYellow",
            BackgroundColor = "#0B0B0D"
        };

        SplashRules.Normalize(splash);

        Assert.Equal("Starting Steam Big Picture…", splash.Text);
        Assert.Equal("Please wait while the handheld finishes waking up — this takes a moment", splash.Caption);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal("#80FF9D3D", splash.CaptionColor);
        Assert.Equal("LightGoldenrodYellow", splash.SpinnerColor);
        Assert.Equal("#0B0B0D", splash.BackgroundColor);
    }

    [Fact]
    public void ImportingASplashThemeKeepsItsTextWhole()
    {
        using var temp = new TemporaryDirectory();
        var themePath = temp.GetPath("long.wsgmsplash");
        var shared = new SplashConfig { Text = new string('T', 1_000), Caption = new string('C', 1_000) };
        Assert.True(SplashTheme.Export(shared, themePath));

        var imported = SplashTheme.Import(themePath, temp.GetPath("staged"));

        Assert.NotNull(imported);
        Assert.Equal(shared.Text, imported.Text);
        Assert.Equal(shared.Caption, imported.Caption);
    }

    [Fact]
    public void LoadingAConfigWithAnAbsurdSplashSizeClampsItOnTheLoadPath()
    {
        // Normalize is what ConfigStore.Read runs over a persisted config.json.
        var config = new AppConfig
        {
            Splash = new SplashConfig { SpinnerSize = 2147483647, LogoMaxSize = 999999 }
        };

        var splash = AppConfigRules.Normalize(config).Value.Splash;

        Assert.Equal(1024, splash.SpinnerSize);
        Assert.Equal(4096, splash.LogoMaxSize);
    }

    [Fact]
    public void AWriterTransactionHoldsThePrivateConfigMutexUntilItIsDisposed()
    {
        // The store runs over a temporary root and a private mutex name, so this never touches
        // the production Local\WSGM.Config lock or the developer's real configuration.
        using var temporary = new TemporaryConfigStore();

        using (temporary.Store.Transaction())
        {
            Assert.False(MutexTakenOnAnotherThread(temporary.Context.ConfigMutexName, 200));
        }

        Assert.True(MutexTakenOnAnotherThread(temporary.Context.ConfigMutexName, 2000));
    }

    [Fact]
    public void ANestedWriterOrReadOnTheWriterThreadIsRefusedWithoutReleasingTheOuterLock()
    {
        // One explicit writer scope: a nested transaction would re-enter the mutex and a nested
        // read would see a document the outer writer is still editing.
        using var temporary = new TemporaryConfigStore();

        using (temporary.Store.Transaction())
        {
            Assert.Throws<ConfigUnavailableException>(() => temporary.Store.Transaction());
            Assert.Equal(ConfigReadOutcome.Unreadable, temporary.Store.Read().Outcome);
            Assert.False(MutexTakenOnAnotherThread(temporary.Context.ConfigMutexName, 200));
        }

        Assert.Equal(ConfigReadOutcome.Absent, temporary.Store.Read().Outcome);
    }

    [Fact]
    public void DisposingAWriterTwiceReleasesTheMutexExactlyOnce()
    {
        using var temporary = new TemporaryConfigStore();
        var transaction = temporary.Store.Transaction();

        transaction.Dispose();
        transaction.Dispose();

        Assert.True(MutexTakenOnAnotherThread(temporary.Context.ConfigMutexName, 2000));
        using (temporary.Store.Transaction())
        {
            Assert.False(MutexTakenOnAnotherThread(temporary.Context.ConfigMutexName, 200));
        }
    }

    /// Takes the named mutex from a foreign thread, the only way to observe whether the
    /// cross-process lock is actually held.
    private static bool MutexTakenOnAnotherThread(string name, int timeoutMs)
    {
        var acquired = false;
        var probeThread = new Thread(() =>
        {
            using var probe = new Mutex(false, name);
            try
            {
                acquired = probe.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (acquired)
            {
                probe.ReleaseMutex();
            }
        });
        probeThread.Start();
        probeThread.Join();
        return acquired;
    }

    [Fact]
    public void SplashDefaultsReproduceTheClassicBootSplashLook()
    {
        var splash = new SplashConfig();

        Assert.Equal("#000000", splash.BackgroundColor);
        Assert.False(splash.VignetteEnabled);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.True(splash.TextEnabled);
        Assert.Equal("Please wait", splash.Text);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal(26, splash.TitleFontSize);
        Assert.Equal("", splash.Caption);
        Assert.Equal("#666666", splash.CaptionColor);
        Assert.Equal(12, splash.CaptionFontSize);
        Assert.Equal(SplashSpinnerStyle.Ring, splash.SpinnerStyle);
        Assert.Equal("#FFFFFF", splash.SpinnerColor);
        Assert.Equal(36, splash.SpinnerSize);
        Assert.Equal(SweepEdge.Bottom, splash.SweepEdge);
        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal(200, splash.LogoMaxSize);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, splash.TextPlacement.Anchor);
        Assert.Equal(SplashPlacementMode.WithText, splash.SpinnerPlacement.Mode);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void FullyCustomizedSplashConfigRoundTripsWithStringEnums()
    {
        var original = new AppConfig
        {
            Splash = SplashConfigBuilder.FullyCustomized(@"C:\Images\logo.png", @"C:\Images\bg.png")
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.Contains("\"SpinnerStyle\": \"SweepLine\"", json);
        Assert.NotNull(restored);
        var splash = restored.Splash;
        Assert.Equal("WSGM", splash.Text);
        Assert.False(splash.TextEnabled);
        Assert.Equal("#FF9D3D", splash.TextColor);
        Assert.Equal(48, splash.TitleFontSize);
        Assert.Equal("STARTING STEAM", splash.Caption);
        Assert.Equal("#AAAAAA", splash.CaptionColor);
        Assert.Equal(14, splash.CaptionFontSize);
        Assert.Equal(SplashSpinnerStyle.SweepLine, splash.SpinnerStyle);
        Assert.Equal("#00FF00", splash.SpinnerColor);
        Assert.Equal(72, splash.SpinnerSize);
        Assert.Equal(SweepEdge.Top, splash.SweepEdge);
        Assert.Equal("#101010", splash.BackgroundColor);
        Assert.True(splash.VignetteEnabled);
        Assert.Equal(@"C:\Images\bg.png", splash.BackgroundImagePath);
        Assert.Equal(@"C:\Images\logo.png", splash.LogoImagePath);
        Assert.Equal(320, splash.LogoMaxSize);
        Assert.Equal(SplashPlacementAnchor.BottomLeft, splash.TextPlacement.Anchor);
        Assert.Equal(48, splash.TextPlacement.PaddingX);
        Assert.Equal(160, splash.TextPlacement.PaddingY);
        Assert.Equal(SplashPlacementMode.Absolute, splash.SpinnerPlacement.Mode);
        Assert.Equal(640, splash.SpinnerPlacement.X);
        Assert.Equal(360, splash.SpinnerPlacement.Y);
        Assert.Equal(SplashPlacementMode.Anchor, splash.LogoPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.TopCenter, splash.LogoPlacement.Anchor);
    }

    [Fact]
    public void EveryCefSubToggleDefaultsOnAndRoundTripsOff()
    {
        // Default-on matters as much as the round trip: an older config.json has no
        // Cef section at all, and a sub-toggle that deserialized to false would
        // silently disable a shipped feature on upgrade.
        var defaults = new AppConfig().Cef;
        Assert.True(defaults.Enabled);
        Assert.True(defaults.LibraryTabs);
        Assert.True(defaults.CardManager);
        Assert.True(defaults.SdFormat);
        Assert.True(defaults.WifiIndicator);
        Assert.True(defaults.DownloadKeepAwake);
        Assert.True(defaults.DownloadQueueSort);
        Assert.True(defaults.ConnectedLibraryCarousel);
        // A presentation preference, not a feature: greyed uninstalled games stay out by default.
        Assert.False(defaults.CarouselShowUninstalled);

        var original = new AppConfig
        {
            Cef =
            {
                DownloadQueueSort = false,
                DownloadKeepAwake = false,
                ConnectedLibraryCarousel = false,
                CarouselShowUninstalled = true
            }
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.False(restored.Cef.DownloadQueueSort);
        Assert.False(restored.Cef.DownloadKeepAwake);
        Assert.False(restored.Cef.ConnectedLibraryCarousel);
        Assert.True(restored.Cef.CarouselShowUninstalled);
        Assert.True(restored.Cef.WifiIndicator);
    }

    [Fact]
    public void ACefSectionMissingFromAnOlderConfigStillEnablesTheNewSubToggles()
    {
        // Exactly what an upgrade from a build that predates the sub-toggle sees.
        var restored = JsonSerializer.Deserialize(
            "{\"SteamAutoRelaunch\":true}", ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.NotNull(restored.Cef);
        Assert.True(restored.Cef.DownloadQueueSort);
    }

    [Fact]
    public void AccentColorRoundTripsAndDefaultsToTheWsgmOrange()
    {
        Assert.Equal("#FFFF9D3D", new AppConfig().AccentColor);

        var original = new AppConfig { AccentColor = "#FF2266CC" };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.Equal("#FF2266CC", restored.AccentColor);
    }

    [Fact]
    public void NormalizeKeepsAHandEditedAccentColorAsWritten()
    {
        var config = AppConfigRules.Normalize(new AppConfig { AccentColor = new string('e', 100) }).Value;

        Assert.Equal(new string('e', 100), config.AccentColor);
    }

    [Fact]
    public void NormalizeLeavesEveryAccentColorAUserCanActuallyPickUntouched()
    {
        // Every color a user can pick survives normalizing as written.
        Assert.Equal(
            "#FF9D3D", AppConfigRules.Normalize(new AppConfig { AccentColor = "#FF9D3D" }).Value.AccentColor);
        Assert.Equal(
            "#FFFF9D3D", AppConfigRules.Normalize(new AppConfig { AccentColor = "#FFFF9D3D" }).Value.AccentColor);
        Assert.Equal(
            "LightGoldenrodYellow",
            AppConfigRules.Normalize(new AppConfig { AccentColor = "LightGoldenrodYellow" }).Value.AccentColor);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(8, 8)]
    [InlineData(75, 60)]
    [InlineData(double.NaN, 8)]
    [InlineData(double.PositiveInfinity, 8)]
    public void NormalizeBoundsOverlayBlurForTheNativeBackend(double requested, double expected)
    {
        var config = AppConfigRules.Normalize(new AppConfig { OverlayBlurRadius = requested }).Value;

        Assert.Equal(expected, config.OverlayBlurRadius);
    }

    [Fact]
    public void SourceGeneratedConfigJsonRoundTripsSettingsAndSnapshots()
    {
        var original = new AppConfig
        {
            SteamAutoRelaunch = true,
            StartupDelayMs = 1234,
            GlyphStyle = GlyphStyle.Nintendo,
            PreviousShellSnapshotCaptured = true,
            PreviousShellValueExists = true,
            PreviousShellValue = "explorer.exe",
            StartupApps =
            [
                new StartupAppConfig { Path = @"C:\Tools\companion.exe", Args = "--silent", Elevated = true }
            ],
            SavedDisplayScaleEntries =
            [
                new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }
            ]
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.Contains("\"GlyphStyle\": \"Nintendo\"", json);
        Assert.NotNull(restored);
        Assert.True(restored.SteamAutoRelaunch);
        Assert.Equal(1234, restored.StartupDelayMs);
        Assert.Equal(GlyphStyle.Nintendo, restored.GlyphStyle);
        Assert.Equal("explorer.exe", restored.PreviousShellValue);
        Assert.Single(restored.StartupApps);
        Assert.True(restored.StartupApps[0].Elevated);
        Assert.Equal(150, Assert.Single(restored.SavedDisplayScaleEntries).Percent);
    }

    [Fact]
    public void SignInStartDefaultsMatchTheInstallerIntent()
    {
        var config = new AppConfig();

        Assert.True(config.StartAtSignIn);
        Assert.Equal(SessionStartMode.Game, config.StartMode);
        Assert.Equal(5000, config.ExplorerLogonSettleMs);
    }

    [Fact]
    public void NormalizeRepairsAnOutOfRangeStartMode()
    {
        var config = new AppConfig { StartMode = (SessionStartMode)99 };

        AppConfigRules.Normalize(config);

        Assert.Equal(SessionStartMode.Game, config.StartMode);
    }

    [Fact]
    public void GameModeLaunchDefaultsToTheDefaultKindAndTheEntryArrangement()
    {
        var launch = new AppConfig().GameModeLaunch;

        Assert.Equal(GameModeLaunchKind.Default, launch.Kind);
        Assert.Equal(GameModeReturn.EntryArrangement, launch.Return);
        Assert.Null(launch.GameLayout);
        Assert.Empty(launch.EnterActions);
    }

    [Fact]
    public void NormalizeRepairsOutOfRangeLaunchEnums()
    {
        var config = new AppConfig
        {
            GameModeLaunch =
            {
                Kind = (GameModeLaunchKind)99,
                Return = (GameModeReturn)99
            }
        };

        AppConfigRules.Normalize(config);

        Assert.Equal(GameModeLaunchKind.Default, config.GameModeLaunch.Kind);
        Assert.Equal(GameModeReturn.EntryArrangement, config.GameModeLaunch.Return);
    }

    [Fact]
    public void ALaunchLayoutAndItsActionStepsRoundTrip()
    {
        DisplayTargetIdentity target =
            new(@"\\?\DISPLAY#TV0001", null, null, "Living room TV", 0, 0, 3);
        var original = new AppConfig
        {
            GameModeLaunch =
            {
                Kind = GameModeLaunchKind.Custom,
                GameLayout = new DisplayLayout([
                    new DisplayLayoutOutput(target, 0, 0, 3840, 2160, DisplayRefresh.FromHertz(120),
                        DpiPercent: 150, Hdr: true)
                ]),
                WaitForDisplay = target,
                EnterActions =
                [
                    new PluginActionStep
                    {
                        Plugin = new PluginInstanceIdentity("wsgm.ir", "blaster"),
                        ActionId = "remote-press",
                        Arguments = { ["remote"] = new PluginValue(Text: "hdmi-switch") },
                        TimeoutSeconds = 20
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        var output = Assert.Single(restored.GameModeLaunch.GameLayout!.Outputs);
        Assert.Equal((3840, 2160), (output.Width, output.Height));
        Assert.Equal(120, output.Refresh.Hertz);
        Assert.Equal(150, output.DpiPercent);
        Assert.True(output.Hdr);
        Assert.True(output.IsPrimary);
        Assert.Equal(target, restored.GameModeLaunch.WaitForDisplay);
        var step = Assert.Single(restored.GameModeLaunch.EnterActions);
        Assert.Equal("remote-press", step.ActionId);
        Assert.Equal("hdmi-switch", step.Arguments["remote"].Text);
        Assert.Equal(20, step.TimeoutSeconds);
    }

    [Fact]
    public void NormalizePreservesImmutableKnownDisplayTargetsAcrossRepeatedLoads()
    {
        var target = new DisplayTargetIdentity(@"\\?\guard", null, null, "Guard", 0, 0, 1);
        var known = new KnownDisplay { Target = target };
        var config = new AppConfig { GameModeLaunch = { KnownDisplays = [known], WaitForDisplay = target } };
        var original = JsonSerializer.Serialize(target, ConfigJsonContext.Default.DisplayTargetIdentity);

        AppConfigRules.Normalize(config);
        AppConfigRules.Normalize(config);

        Assert.Same(known, Assert.Single(config.GameModeLaunch.KnownDisplays));
        Assert.Same(target, known.Target);
        Assert.Same(target, config.GameModeLaunch.WaitForDisplay);
        Assert.Equal(original,
            JsonSerializer.Serialize(known.Target!, ConfigJsonContext.Default.DisplayTargetIdentity));

        var restored = AppConfigRules.Normalize(ConfigRepair.Deserialize(
            JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig))).Value;

        Assert.Equal(target, Assert.Single(restored.GameModeLaunch.KnownDisplays).Target);
        Assert.Equal(target, restored.GameModeLaunch.WaitForDisplay);
    }

    [Fact]
    public void NormalizeKeepsALayoutThatCannotDescribeADesktopAndReportsIt()
    {
        DisplayTargetIdentity first =
            new(@"\\?\a", null, null, "A", 0, 0, 1);
        DisplayTargetIdentity second =
            new(@"\\?\b", null, null, "B", 0, 0, 2);
        // Two displays both claiming the origin: no primary can be chosen, so the file was either
        // hand-edited or written by something that did not check.
        var config = new AppConfig
        {
            GameModeLaunch =
            {
                GameLayout = new DisplayLayout([
                    new DisplayLayoutOutput(first, 0, 0, 1920, 1080, DisplayRefresh.FromHertz(60)),
                    new DisplayLayoutOutput(second, 0, 0, 1920, 1080, DisplayRefresh.FromHertz(60))
                ])
            }
        };

        var layout = config.GameModeLaunch.GameLayout;

        var normalized = AppConfigRules.Normalize(config);

        Assert.Same(layout, config.GameModeLaunch.GameLayout);
        Assert.Same(first, layout!.Outputs[0].Target);
        Assert.Same(second, layout.Outputs[1].Target);
        Assert.Contains(normalized.Diagnostics,
            diagnostic => diagnostic.Contains("GameLayout", StringComparison.Ordinal));

        var restored = AppConfigRules.Normalize(ConfigRepair.Deserialize(
            JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig)));

        Assert.Equal(layout.Outputs.ToArray(), restored.Value.GameModeLaunch.GameLayout!.Outputs.ToArray());
        Assert.Contains(restored.Diagnostics,
            diagnostic => diagnostic.Contains("GameLayout", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeDropsAnActionStepThatNamesNoPluginAndClampsTheDeadline()
    {
        var config = new AppConfig
        {
            GameModeLaunch =
            {
                EnterActions =
                [
                    new PluginActionStep { ActionId = "orphan" },
                    new PluginActionStep
                    {
                        Plugin = new PluginInstanceIdentity("wsgm.ir", "blaster"), ActionId = "press",
                        TimeoutSeconds = 9000
                    }
                ]
            }
        };

        AppConfigRules.Normalize(config);

        var step = Assert.Single(config.GameModeLaunch.EnterActions);
        Assert.Equal("press", step.ActionId);
        Assert.Equal(120, step.TimeoutSeconds);
    }

    [Fact]
    public void SignInStartFieldsRoundTripThroughSourceGeneratedJson()
    {
        var original = new AppConfig
        {
            StartAtSignIn = false,
            StartMode = SessionStartMode.Desktop,
            ExplorerLogonSettleMs = 250
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.False(restored.StartAtSignIn);
        Assert.Equal(SessionStartMode.Desktop, restored.StartMode);
        Assert.Equal(250, restored.ExplorerLogonSettleMs);
    }

    [Fact]
    public void NormalizeDropsNullLaunchWrapperEntriesAndRepairsTheirStrings()
    {
        var config = new AppConfig
        {
            LaunchWrappers =
            [
                null!,
                new LaunchWrapperConfig
                {
                    AppId = 7,
                    OriginalTarget = null!,
                    OriginalLaunchOptions = null!,
                    OriginalStartDir = null!,
                    Name = null!,
                    CustomActionPath = null!,
                    CustomArguments = null!
                }
            ]
        };

        var normalized = AppConfigRules.Normalize(config).Value;

        var wrapper = Assert.Single(normalized.LaunchWrappers);
        Assert.Equal(7, wrapper.AppId);
        Assert.Equal("", wrapper.OriginalTarget);
        Assert.Equal("", wrapper.OriginalLaunchOptions);
        Assert.Equal("", wrapper.OriginalStartDir);
        Assert.Equal("", wrapper.Name);
        Assert.Equal("", wrapper.CustomActionPath);
        Assert.Equal("", wrapper.CustomArguments);
    }

    /// <summary>
    ///     The upgrade guarantee. Every config.json written before Steam Input
    ///     Management existed omits the property, and those devices must come up with the
    ///     shim deploying - otherwise an upgrade silently costs them controller
    ///     navigation in the overlay.
    /// </summary>
    [Fact]
    public void AConfigWrittenBeforeSteamInputManagementDeserializesWithItOn()
    {
        var restored = JsonSerializer.Deserialize(
            """{"SteamInputLeaseEnabled":true}""", ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.True(restored.SteamInputManagementEnabled);
    }

    [Fact]
    public void AnExplicitlyDisabledSteamInputManagementSurvivesARoundTrip()
    {
        var original = new AppConfig { SteamInputManagementEnabled = false };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.False(restored!.SteamInputManagementEnabled);
    }

    /// <summary>
    ///     The opt-out has to survive a round trip on its own: it is read once
    ///     when a focused surface opens, so a value that failed to persist would silently
    ///     re-enable the lease at the next overlay open rather than at some visible moment.
    /// </summary>
    [Fact]
    public void AnExplicitlyDisabledSteamInputLeaseSurvivesARoundTrip()
    {
        var original = new AppConfig { SteamInputLeaseEnabled = false };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.False(restored!.SteamInputLeaseEnabled);
        Assert.True(restored.SteamInputManagementEnabled);
    }

    [Fact]
    public void MissingBottomBindingIsDisabledWithoutChangingTopBinding()
    {
        var gestures = JsonSerializer.Deserialize<GestureConfig>("{}")!;
        Assert.False(gestures.BottomEdge);
        Assert.True(gestures.TopEdge);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitBottomChoiceSurvivesConfigurationRoundTrip(bool enabled)
    {
        var json = JsonSerializer.Serialize(new GestureConfig { BottomEdge = enabled });
        Assert.Equal(enabled, JsonSerializer.Deserialize<GestureConfig>(json)!.BottomEdge);
    }

    [Fact]
    public void NewConfigurationsKeepBottomDisabledAndTopAvailable()
    {
        var gestures = new GestureConfig();

        Assert.False(gestures.BottomEdge);
        Assert.True(gestures.TopEdge);
        Assert.True(gestures.LeftEdgeSteamMenu);
        Assert.True(gestures.RightEdgeSteamQuickAccess);
    }

    [Fact]
    public void NormalizeDropsBlankAndDuplicatePins()
    {
        var config = new AppConfig
            { QuickAccessPins = ["system.keep-awake", "", " ", "system.keep-awake", "home.steam"] };

        AppConfigRules.Normalize(config);

        Assert.Equal(["system.keep-awake", "home.steam"], config.QuickAccessPins);
    }

    [Fact]
    public void NormalizeRepairsANullPinList()
    {
        Assert.Equal(["home.desktop"], new AppConfig().QuickAccessPins);
        var config = new AppConfig { QuickAccessPins = null! };

        AppConfigRules.Normalize(config);

        Assert.NotNull(config.QuickAccessPins);
        Assert.Empty(config.QuickAccessPins);
    }

    [Fact]
    public void NormalizeKeepsExistingNestedSectionsAndCollections()
    {
        var apps = new List<StartupAppConfig>();
        var hotkey = new HotkeyConfig { Enabled = true, VirtualKey = 0x41 };
        var chord = new GamepadChordConfig { Enabled = true, Buttons = (int)GamepadButtons.A };
        var gestures = new GestureConfig { BottomEdge = true };
        var textPlacement = new SplashElementPlacement { Anchor = SplashPlacementAnchor.BottomCenter };
        var spinnerPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.Absolute, X = 10, Y = 20 };
        var logoPlacement = new SplashElementPlacement { Mode = SplashPlacementMode.Anchor };
        var splash = new SplashConfig
        {
            Text = "Custom",
            TextPlacement = textPlacement,
            SpinnerPlacement = spinnerPlacement,
            LogoPlacement = logoPlacement
        };
        var config = new AppConfig
        {
            StartupApps = apps,
            Hotkey = hotkey,
            GamepadChord = chord,
            Gestures = gestures,
            Splash = splash,
            AccentColor = "#FF123456"
        };

        var normalized = AppConfigRules.Normalize(config).Value;

        Assert.Same(config, normalized);
        Assert.Same(apps, normalized.StartupApps);
        Assert.Same(hotkey, normalized.Hotkey);
        Assert.Same(chord, normalized.GamepadChord);
        Assert.Same(gestures, normalized.Gestures);
        Assert.Same(splash, normalized.Splash);
        Assert.Same(textPlacement, normalized.Splash.TextPlacement);
        Assert.Same(spinnerPlacement, normalized.Splash.SpinnerPlacement);
        Assert.Same(logoPlacement, normalized.Splash.LogoPlacement);
        Assert.Equal("Custom", normalized.Splash.Text);
        Assert.Equal("#FF123456", normalized.AccentColor);
    }

    [Fact]
    public void Normalize_NewInstallStartsSingleplayerTitlesOnTheSteamOverlay()
    {
        // The recorded decision: a title nothing says is multiplayer takes the Steam integration route.
        var library = AppConfigRules.Normalize(new AppConfig()).Value.GameLibrary;

        Assert.Equal(ImportMode.SteamIntegration, library.DefaultMode);
        Assert.False(library.ImportUnroutable);
    }

    [Fact]
    public void Normalize_RepairsAGameLibraryModeNoReleaseHas()
    {
        var config = AppConfigRules.Normalize(new AppConfig
        {
            GameLibrary = new GameLibraryConfig { DefaultMode = (ImportMode)42 }
        }).Value;

        Assert.Equal(ImportMode.SteamIntegration, config.GameLibrary.DefaultMode);
    }
}
