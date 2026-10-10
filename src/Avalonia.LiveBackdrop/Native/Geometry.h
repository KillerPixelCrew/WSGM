#pragma once

// Every rectangle here is in physical screen pixels. The shared multi-window visual maps
// source screen coordinates to a destination of exactly the same size, without scaling.
inline RECT ClientScreenBounds(const POINT& origin, const RECT& client)
{
    return {origin.x, origin.y, origin.x + client.right - client.left,
        origin.y + client.bottom - client.top};
}

inline bool ShellThumbnailBounds(const RECT& shell, const RECT& source, RECT& crop, RECT& destination)
{
    RECT visible{};
    if (!IntersectRect(&visible, &shell, &source)) return false;
    crop = visible;
    OffsetRect(&crop, -shell.left, -shell.top);
    destination = visible;
    OffsetRect(&destination, -source.left, -source.top);
    return true;
}
