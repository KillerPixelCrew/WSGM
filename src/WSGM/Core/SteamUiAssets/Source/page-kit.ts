// What WSGM's tabbed store pages (Themes, Animations) share and differ in only by their patch id.
//
// Function declarations, so a page fragment that sorts ahead of this one can call them at its top
// level: the fragments are one scope, and these are hoisted to its start.

// A page's command sender. A refusal is explained by the host in its next state and the page draws
// that, so nothing is swallowed here.
function wsgmPageAct(patchId: string) {
  return (command: string, payload: any = {}) => request(patchId, command, payload).catch(() => undefined);
}

// The tabbed frame's active tab, tab switch and banner, sent back as the host's setTab and dismiss
// commands.
function wsgmPageFrame(act: (command: string, payload?: any) => unknown, state) {
  const banner = state.error || state.notice;
  return {
    active: state.activeTab,
    onTab: (tab) => void act("setTab", { tab }),
    banner: banner ? { text: banner, error: !!state.error, onDismiss: () => void act("dismiss") } : null,
  };
}
