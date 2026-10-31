namespace WSGM.Core;

/// <summary>Recognises one engine's folder layout.</summary>
/// <remarks>Detectors only add evidence; adding one never changes installers.</remarks>
internal interface IGameEngineDetector
{
    /// <summary>Adds this engine's evidence when the layout matches.</summary>
    /// <param name="evidence">The shared evidence.</param>
    void Detect(GameRuntimeEvidence evidence);
}
