using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Plugin.IntelGpu.Controls;

/// <summary>One member of a driver enum or flag set, offered as a choice.</summary>
/// <param name="Value">The driver's value.</param>
/// <param name="Id">The stable choice value WSGM stores.</param>
/// <param name="Label">The plain label.</param>
internal sealed record EnumMember(uint Value, string Id, string Label)
{
    /// <summary>The published value, built once so a read allocates nothing.</summary>
    public CapabilityValue Choice { get; } = CapabilityValue.Choice(Id);
}

/// <summary>How an enum's values relate to its supported mask.</summary>
internal enum EnumMaskKind
{
    /// <summary>The mask bit for value <c>v</c> is <c>1 &lt;&lt; v</c>.</summary>
    Ordinal,

    /// <summary>The values are flags themselves, so the mask bit for a value is the value.</summary>
    Flag
}

/// <summary>Lookups over a choice table, as loops over the table so a read allocates nothing.</summary>
internal static class EnumMembers
{
    /// <summary>The member with a choice id.</summary>
    /// <param name="members">The table.</param>
    /// <param name="id">The choice id.</param>
    /// <returns>The member, or null when the table has none by that id.</returns>
    public static EnumMember? ById(IReadOnlyList<EnumMember> members, string? id)
    {
        for (var index = 0; index < members.Count; index++)
        {
            if (string.Equals(members[index].Id, id, StringComparison.Ordinal))
            {
                return members[index];
            }
        }

        return null;
    }

    /// <summary>The member with a driver value.</summary>
    /// <param name="members">The table.</param>
    /// <param name="value">The driver value.</param>
    /// <returns>The member, or null when the table has none with that value.</returns>
    public static EnumMember? ByValue(IReadOnlyList<EnumMember> members, uint value)
    {
        for (var index = 0; index < members.Count; index++)
        {
            if (members[index].Value == value)
            {
                return members[index];
            }
        }

        return null;
    }

    /// <summary>The first member other than zero whose flag a mask sets, in table order.</summary>
    /// <param name="members">The table.</param>
    /// <param name="mask">The flags.</param>
    /// <returns>The member, or null when none is set.</returns>
    public static EnumMember? FirstFlag(IReadOnlyList<EnumMember> members, uint mask)
    {
        for (var index = 0; index < members.Count; index++)
        {
            if (members[index].Value != 0 && (mask & members[index].Value) != 0)
            {
                return members[index];
            }
        }

        return null;
    }

    /// <summary>The mask bit a member occupies.</summary>
    /// <param name="kind">How the enum maps values to bits.</param>
    /// <param name="value">The member's value.</param>
    /// <returns>The bit, or zero when it cannot be represented in 64 bits.</returns>
    public static ulong MaskBit(EnumMaskKind kind, uint value)
    {
        return kind switch
        {
            EnumMaskKind.Flag => value,
            _ => value < 64 ? 1UL << (int)value : 0
        };
    }

    /// <summary>The members whose bit a mask sets.</summary>
    /// <param name="members">The table.</param>
    /// <param name="mask">The driver's supported mask.</param>
    /// <param name="kind">How the enum maps values to bits.</param>
    /// <returns>The members in table order; a member without a representable bit is never supported.</returns>
    public static IReadOnlyList<EnumMember> Supported(IReadOnlyList<EnumMember> members, ulong mask, EnumMaskKind kind)
    {
        List<EnumMember> supported = [];
        foreach (var member in members)
        {
            var bit = MaskBit(kind, member.Value);
            if (bit != 0 && (mask & bit) == bit)
            {
                supported.Add(member);
            }
        }

        return supported;
    }

    /// <summary>The members to offer for a supported mask.</summary>
    /// <param name="members">The documented members.</param>
    /// <param name="mask">The driver's supported mask.</param>
    /// <param name="kind">How the enum maps values to bits.</param>
    /// <returns>
    ///     The members the mask sets. A zero mask, as legacy drivers report, offers every documented member
    ///     and lets a refused write say so.
    /// </returns>
    public static IReadOnlyList<EnumMember> Offered(IReadOnlyList<EnumMember> members, ulong mask, EnumMaskKind kind)
    {
        return mask == 0 ? members : Supported(members, mask, kind);
    }
}
