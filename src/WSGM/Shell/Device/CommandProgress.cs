using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>Where a capability's UI is in the request cycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandProgress>))]
public enum CommandProgress
{
    /// <summary>Nothing is in flight.</summary>
    Idle,

    /// <summary>A command has been sent and no result has come back.</summary>
    Pending,

    /// <summary>The last command finished cleanly.</summary>
    Completed,

    /// <summary>The last command failed or was refused.</summary>
    Failed,

    /// <summary>The last command finished without establishing what the hardware did.</summary>
    Uncertain
}
