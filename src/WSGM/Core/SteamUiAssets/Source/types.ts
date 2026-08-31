type BridgeConfiguration = Readonly<{
  version: number;
  namespace: string;
  binding: string;
  assetHash: string;
  contextGeneration: number;
  documentGeneration: number;
  maximumPending: number;
  timeoutMilliseconds: number;
  allowed: Readonly<Record<string, readonly string[]>>;
}>;

declare const __WSGM_CONFIGURATION_JSON__: BridgeConfiguration;

// This file is a script, not a module, so the interface merges with the global
// Window directly; a `declare global` block would need a module context.
interface Window {
  // Steam's own webpack chunk registry. Untyped by Steam, and the only route to
  // the module runtime the native components are installed into.
  webpackChunksteamui: unknown[];
  [key: string]: any;
}
