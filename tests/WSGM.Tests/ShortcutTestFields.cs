using WSGM.Core;

namespace WSGM.Tests;

internal static class ShortcutTestFields
{
    internal static ShortcutFields Compose(ShortcutRoute route, string launcher)
    {
        Assert.True(CommandShortcut.TryCompose(route, launcher, out var fields, out var refusal), refusal);
        return fields;
    }
}
