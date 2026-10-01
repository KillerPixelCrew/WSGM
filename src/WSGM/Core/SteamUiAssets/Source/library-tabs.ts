// WSGM's library transform shares the toolkit's React claim. It owns no dispatcher property.
const libraryTabsClaim = (() => {
  const name = "wsgm.library-tabs";
  let react: any = null;
  return {
    install(host: any, transform: (value: any) => any) {
      if (react && react !== host) {
        const released = releaseMemo(react, name);
        if (!released.ok) return released;
      }
      const installed = interceptMemo(host, name, transform);
      if (installed.ok) react = host;
      return installed;
    },
    status() {
      return { installed: !!react && memoIntercepted(react, name) };
    },
    remove() {
      const released = releaseMemo(react, name);
      if (released.ok) react = null;
      return released;
    },
  };
})();
registerGate("wsgmLibraryTabs", libraryTabsClaim);
