using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Controls;

internal enum PhysicalGlyphSurface
{
    DeviceDescription,
    NavigationHint
}

internal enum PhysicalGlyphTheme
{
    Light,
    Dark,
    HighContrast
}

internal sealed record PhysicalGlyphPath(
    Geometry Geometry,
    string Fill,
    string Stroke,
    decimal StrokeWidth,
    string StrokeLineCap,
    string StrokeLineJoin);

internal sealed record PhysicalGlyphRenderPlan
{
    internal required GlyphControlId? PhysicalControl { get; init; }
    internal required PhysicalGlyphFallbackReason FallbackReason { get; init; }
    internal required GlyphViewBox? ViewBox { get; init; }
    internal required IReadOnlyList<PhysicalGlyphPath> Paths { get; init; }
    internal required ReadOnlyMemory<byte> RasterPng { get; init; }

    internal bool UsesDeviceArtwork => Paths.Count > 0 || !RasterPng.IsEmpty;
}

/// <summary>
///     Bounded, path-free adapter from an imported physical profile to Avalonia-safe geometry plans.
/// </summary>
/// <remarks>
///     The service never opens a package file, parses SVG, or performs network work; it consumes only
///     the normalized model returned by the SDK's bounded package loader.
/// </remarks>
internal sealed class PhysicalGlyphService : IDisposable
{
    private const int DefaultMaximumCacheEntries = 128;
    private const int DefaultMaximumCacheBytes = 4 * 1024 * 1024;
    private readonly Dictionary<RenderCacheKey, CacheEntry> _cache = [];
    private readonly PhysicalGlyphCatalog _catalog;

    private readonly Lock _gate = new();
    private readonly LinkedList<RenderCacheKey> _lru = [];
    private readonly int _maximumCacheBytes;
    private readonly int _maximumCacheEntries;
    private int _cacheBytes;

    internal PhysicalGlyphService(
        PhysicalGlyphCatalog catalog,
        int maximumCacheEntries = DefaultMaximumCacheEntries,
        int maximumCacheBytes = DefaultMaximumCacheBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheBytes);

        _catalog = catalog;
        _maximumCacheEntries = maximumCacheEntries;
        _maximumCacheBytes = maximumCacheBytes;
        _catalog.Changed += ResetCache;
    }

    internal int CachedEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    internal int CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _cacheBytes;
            }
        }
    }

    public void Dispose()
    {
        _catalog.Changed -= ResetCache;
        ResetCache();
    }

    internal PhysicalGlyphRenderPlan Resolve(
        PhysicalGlyphSelectionResult selection,
        GlyphControlId requestedControl,
        PhysicalGlyphSurface surface,
        bool activeInputSourceIsManagedHandheld,
        PhysicalGlyphTheme theme,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Profile is null)
        {
            return FallbackPlan(selection.FallbackReason);
        }

        var authorized = surface switch
        {
            PhysicalGlyphSurface.DeviceDescription => true,
            PhysicalGlyphSurface.NavigationHint => activeInputSourceIsManagedHandheld,
            _ => false
        };
        if (!authorized)
        {
            return FallbackPlan(PhysicalGlyphFallbackReason.SourceNotHandheld);
        }

        var scaleBucket = Math.Clamp((int)Math.Round(scale * 4, MidpointRounding.AwayFromZero), 2, 16);
        RenderCacheKey key = new(
            selection.Profile.Manifest.ProfileId,
            selection.Profile.Manifest.Revision,
            requestedControl,
            theme,
            scaleBucket);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                return cached.Plan;
            }

            var plan = BuildPlan(selection.Profile, requestedControl);
            var cost = EstimateCost(selection.Profile, plan);
            if (cost > _maximumCacheBytes)
            {
                return plan;
            }

            var node = _lru.AddFirst(key);
            _cache.Add(key, new CacheEntry(plan, cost, node));
            _cacheBytes += cost;
            TrimCache();
            return plan;
        }
    }

    private void ResetCache()
    {
        lock (_gate)
        {
            ClearCacheLocked();
        }
    }

    private static PhysicalGlyphRenderPlan BuildPlan(
        ImportedGlyphProfile profile,
        GlyphControlId requestedControl)
    {
        var physicalControl = requestedControl;
        var alias = profile.Manifest.Aliases.FirstOrDefault(item => item.LogicalControl == requestedControl);
        if (alias is not null)
        {
            physicalControl = alias.PhysicalControl;
        }

        var mapping = profile.Manifest.Controls.FirstOrDefault(item => item.Control == physicalControl);
        if (mapping is null || mapping.Presence is GlyphControlPresence.Absent)
        {
            return FallbackPlan(
                mapping is null
                    ? PhysicalGlyphFallbackReason.ArtworkMissing
                    : PhysicalGlyphFallbackReason.ControlAbsent,
                physicalControl);
        }

        if (mapping.AssetSha256 is not { } hash
            || !profile.Assets.TryGetValue(hash, out var asset))
        {
            return FallbackPlan(
                PhysicalGlyphFallbackReason.ArtworkMissing,
                physicalControl);
        }

        if (asset.Vector is not { } vector)
        {
            return new PhysicalGlyphRenderPlan
            {
                PhysicalControl = physicalControl,
                FallbackReason = PhysicalGlyphFallbackReason.None,
                ViewBox = null,
                Paths = [],
                RasterPng = asset.RasterPng
            };
        }

        try
        {
            var paths = vector.Paths.Select(path => new PhysicalGlyphPath(
                StreamGeometry.Parse(FillRulePrefix(path.FillRule) + ToAvaloniaPathData(path.Data)),
                path.Fill,
                path.Stroke,
                path.StrokeWidth,
                path.StrokeLineCap,
                path.StrokeLineJoin)).ToArray();
            return new PhysicalGlyphRenderPlan
            {
                PhysicalControl = physicalControl,
                FallbackReason = PhysicalGlyphFallbackReason.None,
                ViewBox = vector.ViewBox,
                Paths = paths,
                RasterPng = default
            };
        }
        catch (Exception)
        {
            // The importer accepted only its strict path grammar, but Avalonia is the final
            // renderer authority. A parser-version disagreement is a bounded fallback, never a
            // reason to expose source SVG bytes or take down the overlay.
            return FallbackPlan(
                PhysicalGlyphFallbackReason.RenderRejected,
                physicalControl);
        }
    }

    // Avalonia's path grammar defaults to even-odd filling, while SVG defaults to non-zero; the
    // leading fill-rule command keeps the artwork's own rule.
    private static string FillRulePrefix(string fillRule)
    {
        return string.Equals(fillRule, "evenodd", StringComparison.Ordinal) ? "F0 " : "F1 ";
    }

    private static int EstimateCost(
        ImportedGlyphProfile profile,
        PhysicalGlyphRenderPlan plan)
    {
        if (plan.PhysicalControl is not { } control)
        {
            return 64;
        }

        var mapping = profile.Manifest.Controls.FirstOrDefault(item => item.Control == control);
        return mapping?.AssetSha256 is { } hash
               && profile.Assets.TryGetValue(hash, out var asset)
            ? Math.Max(64, asset.RetainedBytes)
            : 64;
    }

    private static string ToAvaloniaPathData(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        StringBuilder output = new(normalized.Length + 16);
        var index = 0;
        while (index < tokens.Length)
        {
            var command = tokens[index++];
            if (output.Length > 0)
            {
                output.Append(' ');
            }

            output.Append(command);
            var arity = char.ToUpperInvariant(command[0]) switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'C' => 6,
                'S' or 'Q' => 4,
                'A' => 7,
                'Z' => 0,
                _ => throw new FormatException("Imported glyph path has an unsupported command.")
            };
            while (arity > 0 && index < tokens.Length && !char.IsAsciiLetter(tokens[index][0]))
            {
                if (index + arity > tokens.Length)
                {
                    throw new FormatException("Imported glyph path has an incomplete command.");
                }

                output.Append(' ');
                switch (arity)
                {
                    case 1:
                        output.Append(tokens[index]);
                        break;
                    case 7:
                        output.Append(tokens[index]).Append(',').Append(tokens[index + 1])
                            .Append(' ').Append(tokens[index + 2])
                            .Append(' ').Append(tokens[index + 3])
                            .Append(' ').Append(tokens[index + 4])
                            .Append(' ').Append(tokens[index + 5]).Append(',').Append(tokens[index + 6]);
                        break;
                    default:
                    {
                        for (var parameter = 0; parameter < arity; parameter += 2)
                        {
                            if (parameter > 0)
                            {
                                output.Append(' ');
                            }

                            output.Append(tokens[index + parameter]).Append(',')
                                .Append(tokens[index + parameter + 1]);
                        }

                        break;
                    }
                }

                index += arity;
            }
        }

        return output.ToString();
    }

    private static PhysicalGlyphRenderPlan FallbackPlan(
        PhysicalGlyphFallbackReason reason,
        GlyphControlId? physicalControl = null)
    {
        return new PhysicalGlyphRenderPlan
        {
            PhysicalControl = physicalControl,
            FallbackReason = reason,
            ViewBox = null,
            Paths = [],
            RasterPng = default
        };
    }

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void TrimCache()
    {
        while (_cache.Count > _maximumCacheEntries || _cacheBytes > _maximumCacheBytes)
        {
            var tail = _lru.Last;
            if (tail is null
                || !_cache.Remove(tail.Value, out var removed))
            {
                break;
            }

            _lru.Remove(tail);
            _cacheBytes -= removed.Cost;
        }
    }

    private void ClearCacheLocked()
    {
        _cache.Clear();
        _lru.Clear();
        _cacheBytes = 0;
    }

    // ReSharper disable NotAccessedPositionalProperty.Local
    private readonly record struct RenderCacheKey(
        string ProfileId,
        int Revision,
        GlyphControlId Control,
        PhysicalGlyphTheme Theme,
        int ScaleBucket);
    // ReSharper restore NotAccessedPositionalProperty.Local

    private sealed record CacheEntry(
        PhysicalGlyphRenderPlan Plan,
        int Cost,
        LinkedListNode<RenderCacheKey> Node);
}
