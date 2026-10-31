namespace WSGM.Shell;

/// <summary>What the capability hook publishes: the bits to clear and the controller to clear them on.</summary>
/// <param name="Mask">The capability bits to clear, as a decimal string because they exceed 32 bits.</param>
/// <param name="VendorId">The USB vendor id of the controller the mask applies to.</param>
/// <param name="ProductId">The USB product id of the controller the mask applies to.</param>
internal sealed record WsgmControllerCapsState(string Mask, int VendorId, int ProductId);
