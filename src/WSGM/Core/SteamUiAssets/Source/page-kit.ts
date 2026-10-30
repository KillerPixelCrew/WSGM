/**
 * Creates a command sender for pages whose error banner comes from host state.
 * @param patchId Registered WSGM page command identity.
 * @returns A sender resolving to the backend result, or undefined on rejection; the page relies on host publication for error display.
 */
function wsgmPageAct(patchId: string) {
  return (command: string, payload: any = {}) => request(patchId, command, payload).catch(() => undefined);
}

/**
 * Projects published page navigation and notices into the shared tabbed frame.
 * @param act Page sender accepting setTab and dismiss commands.
 * @param state Published activeTab, error and notice fields.
 * @returns Frame props; errors take precedence over notices and no message yields a null banner.
 */
function wsgmPageFrame(act: (command: string, payload?: any) => unknown, state) {
  const banner = state.error || state.notice;
  return {
    active: state.activeTab,
    onTab: (tab) => void act("setTab", { tab }),
    banner: banner ? { text: banner, error: !!state.error, onDismiss: () => void act("dismiss") } : null,
  };
}

/**
 * Formats a byte count exactly as the Overlay's LibraryManagerService.FormatBytes does, so a size or
 * download amount reads the same on both surfaces. Units are binary: from one gibibyte (1024^3
 * bytes) up it is gigabytes rounded to one decimal with a trailing ".0" dropped, and below that it
 * is whole megabytes (1024^2 bytes), rounded to the nearest.
 * @param bytes The non-negative byte count.
 * @returns The count with its unit, such as "512 MB", "1 GB" or "14.6 GB".
 */
function wsgmFormatBytes(bytes: number) {
  const gigabyte = 1024 * 1024 * 1024;
  return bytes >= gigabyte
    ? `${(bytes / gigabyte).toFixed(1).replace(/\.0$/, "")} GB`
    : `${Math.round(bytes / (1024 * 1024))} MB`;
}
