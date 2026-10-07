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
