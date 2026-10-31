namespace WSGM.Core;

/// <summary>The browser engine's actual resource lifetime, separate from page navigation.</summary>
internal enum BrowserLifecycle
{
    NotCreated,
    Creating,
    Active,
    Retained,
    Destroying,
    Failed
}
