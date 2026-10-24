#pragma once

// Window placement uses physical screen pixels; thumbnail source dimensions belong to DWM.
inline RECT ClientScreenBounds(const POINT& origin, const RECT& client)
{
    return {origin.x, origin.y, origin.x + client.right - client.left,
        origin.y + client.bottom - client.top};
}

inline bool WindowThumbnailBounds(const RECT& window, const SIZE& surface, const RECT& client, RECT& crop, RECT& destination, POINT& offset)
{
    RECT visible{};
    if (surface.cx <= 0 || surface.cy <= 0 || !IntersectRect(&visible, &window, &client)) return false;
    const auto width = window.right - window.left;
    const auto height = window.bottom - window.top;
    crop = {0, 0, surface.cx, surface.cy};
    destination = {0, 0, width, height};
    offset = {window.left - client.left, window.top - client.top};
    return true;
}
