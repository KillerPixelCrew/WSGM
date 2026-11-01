using System;

namespace WSGM.Core;

/// <summary>An installed Windows power scheme. Only the GUID identifies the scheme.</summary>
/// <param name="Id">Locale-independent identity, suitable for persisted references.</param>
/// <param name="Name">Windows' localized display name.</param>
internal sealed record PowerScheme(Guid Id, string Name);
