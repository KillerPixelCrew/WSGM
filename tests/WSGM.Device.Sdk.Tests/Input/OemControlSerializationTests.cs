using System.Text.Json;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Tests;

public sealed class OemControlSerializationTests
{
    [Theory]
    [InlineData(OemPressKind.Short, "Short", 0)]
    [InlineData(OemPressKind.Long, "Long", 1)]
    public void PressKindsWriteNamesAndStillReadLegacyNumbers(OemPressKind kind, string name, int number)
    {
        Assert.Equal(number, (int)kind);
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(kind));
        Assert.Equal(kind, JsonSerializer.Deserialize<OemPressKind>($"\"{name}\""));
        Assert.Equal(kind, JsonSerializer.Deserialize<OemPressKind>(number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
