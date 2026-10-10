#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cassert>
#include "../../src/Avalonia.LiveBackdrop/Native/Geometry.h"

static void Equal(const RECT& actual, const RECT& expected)
{
    assert(EqualRect(&actual, &expected));
}

int main()
{
    // A physical client on a monitor left of and above the primary output keeps its
    // negative origin. Its shell crop and application crop cover the identical pixels.
    const auto bounds = ClientScreenBounds({-2300, -1100}, {0, 0, 1800, 1200});
    Equal(bounds, {-2300, -1100, -500, 100});
    RECT crop{}, destination{};
    assert(ShellThumbnailBounds({-2560, -1440, 0, 0}, bounds, crop, destination));
    Equal(crop, {260, 340, 2060, 1440});
    Equal(destination, {0, 0, 1800, 1100});
    assert(!ShellThumbnailBounds({0, 0, 3840, 2160}, bounds, crop, destination));

    // A client spanning differently scaled outputs uses physical pixels throughout.
    // Both shell surfaces meet at destination x=400 without rescaling either monitor.
    const auto spanning = ClientScreenBounds({-400, 200}, {0, 0, 2400, 1500});
    assert(ShellThumbnailBounds({-2560, 0, 0, 1440}, spanning, crop, destination));
    Equal(crop, {2160, 200, 2560, 1440});
    Equal(destination, {0, 0, 400, 1240});
    assert(ShellThumbnailBounds({0, 0, 3840, 2160}, spanning, crop, destination));
    Equal(crop, {0, 200, 2000, 1700});
    Equal(destination, {400, 0, 2400, 1500});

    // Moving to the other monitor changes only the screen crop, not its local size.
    const auto moved = ClientScreenBounds({100, 300}, {0, 0, 1800, 1200});
    assert(ShellThumbnailBounds({0, 0, 3840, 2160}, moved, crop, destination));
    Equal(crop, {100, 300, 1900, 1500});
    Equal(destination, {0, 0, 1800, 1200});
    assert(!ShellThumbnailBounds({-2560, 0, 0, 1440}, moved, crop, destination));
    return 0;
}
