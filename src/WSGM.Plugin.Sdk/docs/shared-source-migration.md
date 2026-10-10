# Shared source ownership

Only shared records and helpers with actual common-plugin or WSGM consumers remain in this SDK. The
old blanket Device SDK source merge has been pruned: device plugin entry points, runtime services,
recovery, identity duplication, input/motion, Windows transports and device preference manifests are
removed. LibHandheld owns the built-in hardware implementation and public input contracts. Device
Lab owns its evidence and lab transport helpers.

[Retained shared contracts](shared-reference.md) records each remaining group's consumer. Common
plugin API admission has a floor of 5 and still honors a declared compatible minimum/maximum range;
an additive API bump does not require exact equality of both range endpoints.

There is no conversion of device archives into built-in support and no legacy preference migration.
Fresh exact device metadata and authored profiles are owned by the application. Source removal does
not change common-plugin settings, actions, Steam CEF extensions or their independent enablement.
