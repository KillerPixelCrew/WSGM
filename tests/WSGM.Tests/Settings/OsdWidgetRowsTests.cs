using WSGM.Core;
using WSGM.Settings;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class OsdWidgetRowsTests
{
    [Fact]
    public void RowsListTheSavedOrderAndAWidgetTheOrderLeavesOutComesLastAsHidden()
    {
        var config = new AppConfig
        {
            Performance = new PerformanceConfig { OsdCustomOrder = "fps,CPU", OsdCustomFps = 1, OsdCustomGpu = 2 }
        };
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config));

        Assert.Equal(["Framerate", "CPU", "Clock", "GPU", "Memory", "Video memory", "Battery"],
            model.OsdWidgets.Select(static row => row.Name));
        Assert.True(model.OsdWidgets[0].IsMinimal);
        Assert.True(model.OsdWidgets[3].IsHidden);

        var saved = model.CaptureSaveRequest().Values.Performance;
        Assert.Equal("FPS,CPU,Time,GPU,RAM,VRAM,BATT", saved.OsdCustomOrder);
        Assert.Equal(0, saved.OsdCustomGpu);
        // The same overlay: only the framerate (minimal) and the CPU (full) render, in that order.
        Assert.Equal([1, 2, 0, 0, 0, 0, 0],
            [saved.OsdCustomFps, saved.OsdCustomCpu, saved.OsdCustomTime, saved.OsdCustomGpu, saved.OsdCustomRam,
                saved.OsdCustomVram, saved.OsdCustomBattery]);
    }

    [Fact]
    public void MovingAndChoosingADetailChangeWhatSaveWritesAndStopAtTheEnds()
    {
        var config = new AppConfig();
        var model = new SettingsViewModel(config, SettingsTestServices.Inert(config));
        model.RecordSavedState();
        var clock = model.OsdWidgets[0];

        Assert.False(model.MoveOsdWidget(clock, -1));
        Assert.True(model.MoveOsdWidget(clock, 2));
        model.OsdWidgets[0].IsMinimal = true;
        model.RefreshUnsavedChanges();

        var saved = model.CaptureSaveRequest().Values.Performance;
        Assert.Equal("GPU,CPU,Time,VRAM,RAM,BATT,FPS", saved.OsdCustomOrder);
        Assert.Equal(1, saved.OsdCustomGpu);
        Assert.Equal(2, model.UnsavedChangeCount);
    }
}
