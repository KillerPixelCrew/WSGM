using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WSGM.Core;

/// <summary>One stylesheet block a theme injects: a file, a CSS variable, or a component's variable.</summary>
/// <remarks>
///     Mirrors <c>Inject</c> in <c>css_inject.py</c> (b1bc683). CSS Loader appends a block to every
///     matching window when the theme is enabled and removes it when it is not; here a block is
///     enabled or not, and the loader publishes every enabled block in order. The text is read when
///     first needed and rewritten through the class translations; a new translation table drops the
///     text so it is read again.
/// </remarks>
public sealed class ThemeInject
{
    private string? _css;
    private string? _hash;

    /// <summary>Creates a block for a stylesheet file.</summary>
    /// <param name="cssPath">The file's full path.</param>
    /// <param name="targets">The expanded targets the block is for.</param>
    /// <param name="identity">What names the block among every theme's: the theme, the patch option and the file.</param>
    public ThemeInject(string cssPath, IReadOnlyList<string> targets, string identity)
    {
        CssPath = cssPath;
        Targets = targets;
        Id = IdentityOf(identity);
    }

    /// <summary>Creates a block whose text is generated rather than read from a file.</summary>
    /// <param name="targets">The expanded targets the block is for.</param>
    /// <param name="css">The text, or null for a block that is generated later.</param>
    /// <param name="identity">What names the block among every theme's: the theme, the patch and the component.</param>
    public ThemeInject(IReadOnlyList<string> targets, string? css, string identity)
    {
        CssPath = string.Empty;
        Targets = targets;
        Id = IdentityOf(identity);
        _css = css;
    }

    /// <summary>
    ///     The block's identity, derived from what it is rather than from when it was built: the
    ///     toolkit keys its nodes by it and leaves a node whose id and hash it already holds alone,
    ///     so a reload that changed nothing must name every block as it did before.
    /// </summary>
    public string Id { get; }

    /// <summary>The stylesheet's path, or empty for a generated block.</summary>
    public string CssPath { get; }

    /// <summary>The expanded targets, in CSS Loader's vocabulary.</summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>Whether the block should be in Steam's windows right now.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether the block's text is generated rather than read from a file.</summary>
    public bool IsGenerated => CssPath.Length == 0;

    /// <summary>The last error reading the file, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Replaces a generated block's text.</summary>
    /// <param name="css">The new text.</param>
    public void SetGenerated(string css)
    {
        _css = css;
        _hash = null;
    }

    /// <summary>Forgets text read from a file, so the next read goes through the current translations.</summary>
    public void Invalidate()
    {
        if (!IsGenerated)
        {
            _css = null;
            _hash = null;
            LoadError = null;
        }
    }

    /// <summary>The text to inject, read and translated on first use.</summary>
    /// <param name="mappings">The class translations.</param>
    /// <returns>The text, or null when the file could not be read.</returns>
    public string? Load(ThemeClassMappings mappings)
    {
        if (_css is not null)
        {
            return _css;
        }

        if (IsGenerated)
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(CssPath, Encoding.UTF8);
            _css = mappings.Rewrite(text);
            _hash = null;
            LoadError = null;
            return _css;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
            return null;
        }
    }

    /// <summary>A short digest of the text last loaded, for the toolkit to tell a changed block by.</summary>
    /// <returns>Sixteen hex characters, or empty when nothing is loaded.</returns>
    public string Hash()
    {
        if (_hash is not null)
        {
            return _hash;
        }

        if (_css is null)
        {
            return string.Empty;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(_css));
        _hash = Convert.ToHexStringLower(digest.AsSpan(0, 8));
        return _hash;
    }

    private static string IdentityOf(string identity)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
    }
}
