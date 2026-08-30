(() => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  const s = b.nativeComponents.status("resolution");
  return JSON.stringify({
    qsAppend: s.lastAppendQuickSettings,
    perfAppend: s.lastAppend,
    renderOutcomes: s.renderOutcomes,
  });
})();
