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
    const auto client = ClientScreenBounds({-2300, -1100}, {0, 0, 1800, 1200});
    Equal(client, {-2300, -1100, -500, 100});
    RECT crop{}, destination{};
    POINT offset{};

    // A desktop surface spanning a secondary output stays whole. DirectComposition
    // positions it behind the client and clips it; DWM never receives a partial crop.
    assert(WindowThumbnailBounds({-2560, -1440, 0, 0}, {2560, 1440}, client, crop, destination, offset));
    Equal(crop, {0, 0, 2560, 1440});
    Equal(destination, {0, 0, 2560, 1440});
    assert(offset.x == -260 && offset.y == -340);
    assert(!WindowThumbnailBounds({0, 0, 3840, 2160}, {3840, 2160}, client, crop, destination, offset));

    // The redirection surface can have a different DPI from its desktop footprint.
    // Capture its actual pixels, scale to the footprint, then position the retained visual.
    assert(WindowThumbnailBounds({100, 200, 1600, 1100}, {1000, 600}, {0, 0, 2560, 1600},
        crop, destination, offset));
    Equal(crop, {0, 0, 1000, 600});
    Equal(destination, {0, 0, 1500, 900});
    assert(offset.x == 100 && offset.y == 200);

    // Sliding the client changes only the visual offset, never the captured surface.
    assert(WindowThumbnailBounds({0, 0, 2560, 1600}, {2560, 1600}, {0, -800, 2560, 800},
        crop, destination, offset));
    Equal(crop, {0, 0, 2560, 1600});
    Equal(destination, {0, 0, 2560, 1600});
    assert(offset.x == 0 && offset.y == 800);
    assert(!WindowThumbnailBounds({0, 0, 2560, 1600}, {2560, 1600}, {0, -1600, 2560, 0},
        crop, destination, offset));

    assert(!WindowThumbnailBounds({0, 0, 100, 100}, {0, 0}, {0, 0, 100, 100},
        crop, destination, offset));
    return 0;
}
