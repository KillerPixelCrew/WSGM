// Emulator management presentation. All package, BIOS and preference writes belong to EmulatorService.
const emulatorStyles = `
#wsgm-import.wsgm-emulators .wsgm-import-head { padding-bottom:12px; }
#wsgm-import.wsgm-emulators .wsgm-import-body { display:block; overflow:auto; padding-bottom:56px; }
.wsgm-emu-content { display:flex; flex-direction:column; gap:14px; min-width:0; }
.wsgm-emu-tabs { display:flex; gap:8px; flex-wrap:wrap; border-bottom:1px solid #3d4450; padding-bottom:12px; }
.wsgm-emu-tabs .DialogButton { width:auto; min-width:0; height:36px; }
.wsgm-emu-tabs [aria-pressed=true] { background:var(--gpSystemBlue,#1a9fff); color:white; }
.wsgm-emu-toolbar { display:flex; gap:12px; align-items:flex-end; flex-wrap:wrap; }
.wsgm-emu-toolbar .DialogInputLabelGroup { margin:0; }
.wsgm-emu-toolbar .DialogButton { width:auto; min-width:0; }
.wsgm-emu-search { flex:1; min-width:180px; }
.wsgm-emu-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:18px; }
.wsgm-emu-grid .steam-ui-kit-card { width:auto; }
.wsgm-emu-mark { position:absolute; inset:0; display:flex; align-items:center; justify-content:center; font-size:28px; font-weight:700; color:#fff; }
.wsgm-emu-split { display:grid; grid-template-columns:minmax(0,2fr) minmax(240px,1fr); gap:24px; }
.wsgm-emu-column { display:flex; flex-direction:column; gap:14px; min-width:0; }
.wsgm-emu-actions { display:flex; gap:10px; flex-wrap:wrap; }
.wsgm-emu-actions .DialogButton { width:auto; min-width:0; }
.wsgm-emu-fact { display:flex; flex-direction:column; gap:4px; font-size:14px; overflow-wrap:anywhere; }
.wsgm-emu-fact > span:first-child { color:#8b929a; font-size:12px; }
.wsgm-emu-file { display:flex; justify-content:space-between; gap:14px; padding:8px 0; border-top:1px solid #3d4450; overflow-wrap:anywhere; }
.wsgm-emu-badge { align-self:center; white-space:nowrap; font-size:12px; font-weight:600; color:#b8bcbf; }
.wsgm-emu-badge[data-status=Missing], .wsgm-emu-badge[data-status=Partial] { color:#ffc66d; }
.wsgm-emu-badge[data-status="Wrong file"] { color:#ff6d6d; }
.wsgm-emu-badge[data-status=Verified] { color:#a3dca3; }
.wsgm-emu-core { display:grid; grid-template-columns:minmax(160px,1fr) minmax(100px,1fr) auto; gap:16px; align-items:center; padding:10px 12px; }
.wsgm-emu-core.gpfocus { background:#fff; color:#0e141b; }
.wsgm-emu-default { display:grid; grid-template-columns:180px minmax(180px,1fr) minmax(180px,1fr); align-items:center; gap:14px; padding:8px 0; }
.wsgm-emu-default .DialogLabel { display:none; }
.wsgm-emu-default .DialogDropDown { margin:0; }
@media(max-width:1000px) { .wsgm-emu-default { grid-template-columns:130px minmax(120px,1fr) minmax(120px,1fr); } }
@media(max-width:750px) { .wsgm-emu-split { grid-template-columns:1fr; } .wsgm-emu-default { grid-template-columns:1fr; } }
`;

function ImportEmulatorPage({ state, status, onBack }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const progress = useEmulatorProgress();
  const [tab, setTab] = react.useState("installed");
  const [route, setRoute] = react.useState({ kind: "list", id: "" });
  const [channel, setChannel] = react.useState("");
  const [search, setSearch] = react.useState("");
  const [coreSearch, setCoreSearch] = react.useState("");
  const [coreFilter, setCoreFilter] = react.useState("all");
  const [biosId, setBiosId] = react.useState("");
  const [biosReturn, setBiosReturn] = react.useState(null as any);
  const manager = state.emulators ?? { definitions: [], installations: [], offers: [], systemPreferences: [] };
  const bios = state.bios ?? { folder: "", checked: false, systems: [] };
  const installed = manager.installations ?? [];
  const available = manager.definitions.filter((item: any) => !installed.some((entry: any) => entry.definitionId === item.id));
  const selectTab = (value: string) => { setBiosReturn(null); setTab(value); setRoute({kind:"list",id:""}); };
  const back = () => route.kind === "cores" ? setRoute({kind:"installation",id:route.id})
    : route.kind !== "list" ? setRoute({kind:"list",id:""}) : biosReturn
      ? (setTab(biosReturn.tab), setRoute(biosReturn.route), setBiosReturn(null)) : onBack?.();
  const button = (label: string, run: any, idle = true, primary = false) => h(
    primary ? importUi.dialogButtonPrimary : importUi.dialogButton,
    { disabled: idle && progress.busy, onClick: run }, label);
  const actions = (...children: any[]) => h(importUi.focusable, {className:"wsgm-emu-actions","flow-children":"row"},...children);
  const fact = (label: string, value: string) => h("div",{className:"wsgm-emu-fact"},h("span",{},label),h("span",{},value || "—"));
  const box = (label: string, ...children: any[]) => renderSteamUiBox(react,label,...children);
  const badge = (value: string) => h("span",{className:"wsgm-emu-badge","data-status":value},value);
  const offerFor = (entry: any) => manager.offers.find((item: any) => item.definitionId === entry.definitionId && item.channel === entry.channel && item.architecture === entry.architecture);
  const labelFor = (entry: any) => (state.installed ?? []).find((item: any) => item.id === entry.id);
  const systemName = (id: string) => (state.romSystems ?? []).find((item: any) => item.id === id)?.name ?? id;
  const matches = (text: string, value: string) => value.toLowerCase().includes(text.toLowerCase());
  const notes = (offer: any) => offer?.notesUrl ? button("Release notes",()=>void emulatorAct("openEmulatorReleaseNotes", {
    definitionId:offer.definitionId,channel:offer.channel,architecture:offer.architecture}),false) : null;
  const pick = (title: string, mode: string, command: string, extra: any = {}) =>
    void showSteamFilePicker(importUi,{title,mode}).then((path: string | null) => path && void emulatorAct(command,{path,...extra}));
  const setup = (installationId: string, name: string) => showSteamModal(importUi, {
    title:name+" setup", render:(close: any)=>h(ImportEmulatorSetupBody,{installationId,close})});
  const toBios = () => {setBiosReturn({tab,route});setTab("bios");setRoute({kind:"list",id:""});};
  const canBack = route.kind !== "list" || !!biosReturn || typeof onBack === "function";
  const tabs = h(importUi.focusable,{className:"wsgm-emu-tabs","flow-children":"row"},
    ...[["installed",`Installed ${installed.length}`],["available",`Available ${available.length}`],
      ["bios","BIOS & firmware"],["defaults","System defaults"]].map(([id,label])=>h(importUi.dialogButton,
      {key:id,"aria-pressed":tab===id,onClick:()=>selectTab(id)},label)));
  let content: any;
  if (route.kind === "installation" || route.kind === "cores") {
    const entry = installed.find((item: any)=>item.id===route.id);
    if (!entry) {
      content = h("p",{},"That installation is no longer registered. Go Back to the installed list.");
    } else {
      const offer = offerFor(entry);
      const installationId = entry.id;
      const definition = manager.definitions.find((item: any)=>item.id===entry.definitionId);
      const remove = () => showSteamUiConfirm(importUi,{
        title:entry.managed ? "Remove emulator?" : "Forget external installation?",
        confirmLabel:entry.managed ? "Remove" : "Forget",
        text:((state.dependencies ?? []).find((item: any)=>item.installationId===installationId)?.summary ?? "Titles using this installation will need another emulator.") + " ROMs, saves, settings and firmware stay.",
        onConfirm:()=>void request(EmulatorPatchId,"removeEmulator",{installationId}).then(
          ()=>setRoute({kind:"list",id:""}), (error: any)=>emulatorReport({text:importError(error),error:true}))
      });
      const maintenance = box("Maintenance",
        entry.managed ? button("Repair installation",()=>void emulatorAct("repairEmulator",{installationId})) : null,
        button(entry.managed ? "Remove emulator…" : "Forget external installation…",remove),
        h("p",{className:"steam-ui-kit-muted"},"ROMs, saves, settings and firmware stay."));
      const systemFiles = box("System files",
        ...(entry.missingRequirements ?? []).map((text: string)=>h("p",{key:text},text)),
        fact("BIOS folder",bios.folder),button("Open BIOS & firmware",toBios,false),
        definition?.prerequisites?.length ? button("Emulator-specific setup…",()=>setup(installationId,entry.name),false) : null);
      if (route.kind === "cores") {
        const cores = entry.cores ?? [];
        const needFiles = (core: any) => (state.coreStatus ?? []).find((item: any)=>item.installationId===installationId && item.coreId===core.id)?.missing ?? false;
        const found = cores.filter((core: any)=>matches(coreSearch,`${core.name} ${core.id} ${(core.systems ?? []).map(systemName).join(" ")}`));
        const visible = (coreFilter==="all" ? cores : found).filter((core: any)=>coreFilter!=="files" || needFiles(core))
          .filter((core: any)=>coreFilter!=="metadata" || core.metadataMissing);
        content = h("div",{className:"wsgm-emu-split"},
          h("div",{className:"wsgm-emu-column"},h("h2",{},`${entry.name} · Installed cores ${cores.length}`),
            h(importUi.textField,{label:"Search cores or systems",value:coreSearch,onChange:(event: any)=>{setCoreSearch(event?.target?.value ?? "");setCoreFilter("matches");}}),
            actions(...[["all",`All ${cores.length}`],["matches",`Matches ${found.length}`],["files",`Need files ${cores.filter(needFiles).length}`],
              ["metadata",`No metadata ${cores.filter((core: any)=>core.metadataMissing).length}`]].map(([id,label])=>h(importUi.dialogButton,
              {key:id,"aria-pressed":coreFilter===id,onClick:()=>setCoreFilter(id)},label))),
            ...visible.map((core: any)=>h(importUi.focusable,{
              key:core.id,className:"wsgm-emu-core",onActivate:()=>showSteamModal(importUi,{
                title:core.name,render:(close: any)=>renderSteamUiSheet(importUi,{actions:[{id:"close",label:"Close",onClick:close}]},
                  fact("Core",core.path),fact("Systems",(core.systems ?? []).map(systemName).join(", ")),
                  fact("File types",(core.extensions ?? []).join(", ")),
                  fact("Metadata",core.metadataMissing ? "Unavailable; choose the system manually" : "Supplied by the core metadata"),
                  ...(core.requiredFiles ?? []).map((path: string)=>fact("Required",path)))})},
              h("span",{},core.name),h("span",{},(core.systems ?? []).map(systemName).join(", ")),
              badge(core.metadataMissing ? "No metadata" : needFiles(core) ? "Need files" : "Ready")))),
          h("div",{className:"wsgm-emu-column"},
            box("RetroArch",fact("Installed",`${entry.version} · ${entry.channel} · ${cores.length} cores`),
              entry.managed ? button("Update installed cores",()=>void emulatorAct("updateEmulator",{installationId})) : null,notes(offer)),
            systemFiles,maintenance));
      } else {
        content = h("div",{className:"wsgm-emu-split"},
          h("div",{className:"wsgm-emu-column"},h("h2",{},entry.name),
            fact("Installed",`${entry.version} · ${entry.channel} · ${entry.managed ? "Managed" : "External"}`),
            fact("Systems",(entry.systems ?? []).map(systemName).join(" · ")),systemFiles,
            box("Installation",fact("Source",entry.source),fact("Verification",entry.integrity),fact("Architecture",entry.architecture),
              fact("Executable",entry.executablePath),fact("Data",entry.dataPath)),
            entry.cores?.length ? button(`Installed cores (${entry.cores.length})`,()=>setRoute({kind:"cores",id:installationId}),false) : null),
          h("div",{className:"wsgm-emu-column"},
            box(offer?.releaseId && offer.releaseId!==entry.releaseId ? "Update available" : "Release",
              fact("Latest",offer?.error || offer?.version || "Check for updates"),
              offer?.releaseId && offer.releaseId!==entry.releaseId && entry.managed
                ? button(`Update to ${offer.version}`,()=>void emulatorAct("updateEmulator",{installationId}),true,true) : null,
              offer?.releaseId && offer.releaseId!==entry.releaseId ? button("Skip this version",()=>void emulatorAct("ignoreEmulatorVersion",{installationId,releaseId:offer.releaseId})) : null,
              offer?.releaseId===entry.ignoredReleaseId ? h("p",{},"You skipped this release. You can still install it now.") : null,
              notes(offer),h("p",{className:"steam-ui-kit-muted"},"Settings, saves and supplied firmware are preserved.")),maintenance));
      }
    }
  } else if (route.kind === "definition") {
    const definition = manager.definitions.find((item: any)=>item.id===route.id);
    if (!definition) content=h("p",{},"That emulator is no longer listed.");
    else {
      const offer = manager.offers.find((item: any)=>item.definitionId===definition.id && item.channel===channel && item.architecture===state.architecture);
      content=h("div",{className:"wsgm-emu-split"},
        h("div",{className:"wsgm-emu-column"},h("h2",{},definition.name),fact("Source",definition.source),
          fact("Systems",definition.systems.map(systemName).join(" · ")),
          box("Set up from your BIOS folder",fact("BIOS folder",bios.folder),
            ...definition.prerequisites.map((rule: any)=>fact(rule.name,rule.description)),button("Open BIOS & firmware",toBios,false))),
        h("div",{className:"wsgm-emu-column"},box("Install",
          h(importUi.dropdown,{label:"Release channel",rgOptions:definition.channels.map((value: string)=>({data:value,label:value})),
            selectedOption:channel,onChange:(option: any)=>option && setChannel(option.data)}),
          fact("Latest",offer?.error || offer?.version || "Check for updates to read releases"),
          definition.dataPolicy?.hasCores ? h("p",{},"Installs the complete published Windows core catalogue.") : null,
          button(`Install ${channel}`,()=>void emulatorAct("installEmulator",{definitionId:definition.id,channel}),true,true),notes(offer),
          button("Use an existing installation…",()=>pick("Choose emulator executable","file","useExternalEmulator",{definitionId:definition.id}))),
          box("Already installed?",h("p",{},"Register your executable. WSGM will not own its updates or remove its files."))));
    }
  } else if (tab === "defaults") {
    const systems = state.romSystems ?? [];
    const groups: any = {};
    for (const system of systems) {
      const group = system.group || (/^ps/.test(system.id) ? "Sony" : ["megadrive","mastersystem","gamegear","saturn","dreamcast"].includes(system.id) ? "Sega"
        : ["nes","snes","gb","gbc","gba","n64","nds","gamecube","wii","switch"].includes(system.id) ? "Nintendo" : "Other systems");
      (groups[group] ??= []).push(system);
    }
    content=h("div",{className:"wsgm-emu-column"},
      h("p",{},"New ROM libraries start with these choices. Existing libraries and title overrides keep their own selections."),
      ...Object.entries(groups).map(([name,items]: any)=>box(name,...items.map((system: any)=>{
        const choices = (state.choices ?? []).find((item: any)=>item.systemId===system.id)?.installations ?? [];
        const preference = manager.systemPreferences.find((item: any)=>item.systemId===system.id);
        const selected = choices.find((item: any)=>item.id===preference?.installationId);
        return h(importUi.focusable,{key:system.id,className:"wsgm-emu-default","flow-children":"row"},h("span",{},system.name),
          h(importUi.dropdown,{label:`${system.name} emulator`,rgOptions:[{data:"",label:"No default"},...choices.map((item: any)=>({data:item.id,label:item.label}))],
            selectedOption:preference?.installationId ?? "",disabled:progress.busy,onChange:(option: any)=>option && void emulatorAct("setPreferredEmulator",{
              systemId:system.id,installationId:option.data,coreId:choices.find((item: any)=>item.id===option.data)?.defaultCoreId ?? ""})}),
          selected?.requiresCore ? h(importUi.dropdown,{label:`${system.name} core`,rgOptions:selected.cores.map((item: any)=>({data:item.id,label:item.label})),
            selectedOption:preference?.coreId || selected.defaultCoreId,disabled:progress.busy,onChange:(option: any)=>option && void emulatorAct("setPreferredEmulator",{
              systemId:system.id,installationId:selected.id,coreId:option.data})}) : h("span",{className:"steam-ui-kit-muted"},selected ? "Standalone" : "—"));
      }))));
  } else if (tab === "bios") {
    const selected = bios.systems.find((item: any)=>item.id===biosId) ?? bios.systems[0];
    content=h("div",{className:"wsgm-emu-column"},
      h("div",{className:"wsgm-emu-toolbar"},fact("BIOS folder",bios.folder),
        button("Change folder…",()=>pick("Choose BIOS folder","folder","setBiosFolder")),
        button("Add files…",()=>pick("Choose local BIOS files","file","addBiosFiles",{systemId:""})),
        button("Add folder…",()=>pick("Choose local BIOS folder","folder","addBiosFiles",{systemId:""})),
        button("Verify files",()=>void emulatorAct("verifyBios"))),
      h("p",{className:"steam-ui-kit-muted"},"EmuDeck layout · MD5 checked against the bundled retrobios list. Files without a checksum are marked Present."),
      !bios.checked ? h("p",{},"Choose Verify files to check your BIOS folder.") : null,
      h("div",{className:"wsgm-emu-split"},
        h("div",{className:"wsgm-emu-column"},box("Systems your emulators run",
          ...bios.systems.map((system: any)=>h(importUi.dialogButton,{key:system.id,"aria-pressed":selected?.id===system.id,onClick:()=>setBiosId(system.id)},
            h("span",{},system.name),badge(system.status),h("small",{},system.emulators.length ? "Used by "+system.emulators.join(" · ") : "Emulator not installed"))))),
        selected ? h("div",{className:"wsgm-emu-column"},h("h2",{},selected.name),
          box("Files in the BIOS folder",...(selected.files ?? []).filter((file: any)=>selected.id!=="ps2" || file.status!=="Missing").map((file: any)=>h("div",{
            key:file.path,className:"wsgm-emu-file",title:file.md5 ? `MD5 ${file.md5}` : undefined},h("span",{},file.path+(file.optional ? " · Optional" : "")),badge(file.status))),
            selected.id==="ps2" && selected.files.every((file: any)=>file.status==="Missing") ? h("p",{},"Add a supported PS2 BIOS dump. Regional alternatives are accepted by MD5.") : null),
          box("Emulator links",...selected.links.map((link: string)=>h("p",{key:link},link)),
            selected.links.length ? null : h("p",{},"Use Relink emulators to apply this BIOS folder.")),
          actions(button("Add files…",()=>pick("Choose "+selected.name+" files","file","addBiosFiles",{systemId:selected.id})),
            button("Relink emulators",()=>void emulatorAct("relinkBios",{systemId:selected.id}))),
          ...installed.filter((entry: any)=>entry.systems.includes(selected.id) && entry.dataPolicy.prerequisites.some((rule: any)=>rule.nativeInstaller))
            .map((entry: any)=>button("Install firmware for "+entry.name,()=>setup(entry.id,entry.name),false))) : null));
  } else {
    const entries = tab === "installed" ? installed : available;
    const visible = entries.filter((entry: any)=>matches(search,`${entry.name} ${(entry.systems ?? []).map(systemName).join(" ")}`));
    content=h("div",{className:"wsgm-emu-column"},
      h("div",{className:"wsgm-emu-toolbar"},h("div",{className:"wsgm-emu-search"},h(importUi.textField,{
        label:"Search emulators or systems",value:search,onChange:(event: any)=>setSearch(event?.target?.value ?? "")})),
        h("span",{className:"steam-ui-kit-muted"},`${installed.length} installed · ${(state.installed ?? []).filter((item: any)=>item.updateAvailable).length} updates · ${installed.filter((item: any)=>item.missingRequirements.length).length} need setup`)),
      h(importUi.focusable,{className:"wsgm-emu-grid","flow-children":"row"},...visible.map((entry: any)=>{
        const label = labelFor(entry);
        const card = renderSteamUiCard(importUi,{key:entry.id,title:entry.name,
          stats:tab==="installed" ? [{text:entry.version},{text:entry.channel},{text:entry.cores?.length ? `${entry.cores.length} cores` : entry.architecture}] : [],
          badge:label?.updateAvailable || entry.missingRequirements?.length ? {text:label?.badge ?? "Needs setup",warn:true} : null,
          meta:tab==="installed" ? [label?.detail ?? entry.version,label?.badge ?? (entry.managed ? "Not checked" : "External")]
            : [(entry.systems ?? []).map(systemName).join(" · "),"Not installed"],
          onActivate:()=>{setRoute({kind:tab==="installed" ? "installation" : "definition",id:entry.id});setChannel(entry.channels?.[0] ?? "");}});
        // The shared card owns Steam focus. Add a text mark to its empty image surface.
        return react.cloneElement(card,{},...react.Children.toArray(card.props.children).map((child: any,index: number)=>index===0
          ? react.cloneElement(child,{},h("div",{className:"wsgm-emu-mark"},entry.name.replace(" (all cores)","")),...react.Children.toArray(child.props.children)) : child));
      })),
      visible.length ? null : h("p",{},entries.length ? "No emulators match your search." : tab==="installed" ? "No emulators installed. Choose Available to install or register an emulator." : "All supported emulators are installed."),
      tab==="installed" && available.length ? h("p",{className:"steam-ui-kit-muted"},"Not installed: "+available.map((entry: any)=>entry.name).join(" · ")+". Find them under Available.") : null);
  }
  return renderSteamUiLevel(importUi,{className:"wsgm-emu-content",onBack:canBack ? back : undefined},
    h("style",{},emulatorStyles),steamUiKitStyle(react),tabs,status,
    h(importUi.focusable,{className:"wsgm-emu-toolbar","flow-children":"row",
      onOptionsButton:()=>!progress.busy && void emulatorAct("refreshEmulators"),onOptionsActionDescription:"Check for updates"},
      canBack ? button("Back",back,false) : null,
      button("Check for updates",()=>void emulatorAct("refreshEmulators")),
      progress.busy ? button("Stop operation",()=>void emulatorAct("cancel"),false) : null,
      progress.status ? h("span",{role:"status"},progress.status) : null),content);
}
