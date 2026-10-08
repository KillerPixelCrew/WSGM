import { writeFile, mkdir } from "node:fs/promises";
const commit = { sha: "913ba096009ee2d3dc6d8094307d65ce0a96d093" };
const root = `https://raw.githubusercontent.com/Abdess/retrobios/${commit.sha}/`;
const yaml = await (await fetch(root + "platforms/emudeck.yml")).text();
const database = await (await fetch(root + "database.json")).json();
const db = Object.values(database.files);
const mapping = {
  "sony-playstation": ["psx", "PlayStation", true],
  "sony-playstation-2": ["ps2", "PlayStation 2", true],
  "sega-saturn": ["saturn", "Sega Saturn", true],
  "sega-dreamcast": ["dreamcast", "Sega Dreamcast", false],
  "nintendo-ds": ["nds", "Nintendo DS", false],
  "nintendo-nes": ["nes", "Nintendo Entertainment System", false],
  "nintendo-64": ["n64", "Nintendo 64", true],
  "nintendo-gamecube": ["gamecube", "GameCube", true],
  "nintendo-switch": ["switch", "Nintendo Switch", false],
  "sony-psp": ["psp", "PlayStation Portable", false],
  "snk-neogeo": ["arcade", "Neo Geo", true],
  "sega-mega-cd": ["megadrive", "Sega Mega CD", true],
};
const systems = [];
let system, file;
for (const line of yaml.split("\n")) {
  const heading = /^  ([a-z0-9-]+):$/.exec(line);
  if (heading) {
    system = { upstream: heading[1], files: [] };
    systems.push(system);
  }
  const name = /^    - name: (.*)$/.exec(line);
  if (name && system) {
    file = { name: name[1].replace(/^"|"$/g, ""), destination: "", md5: "" };
    system.files.push(file);
  }
  const field = /^      (destination|md5): (.*)$/.exec(line);
  if (field && file) file[field[1]] = field[2].replace(/^['"]|['"]$/g, "");
}
const result = systems
  .filter((s) => mapping[s.upstream])
  .map((s) => {
    const [id, name, any] = mapping[s.upstream];
    const whitelist = s.files.filter((f) => f.md5).map((f) => f.md5);
    let files = s.files
      .filter((f) => f.destination)
      .map((f) => ({
        path: f.destination,
        md5: [
          ...new Set(
            db.filter((d) => d.name.toLowerCase() === f.name.toLowerCase()).map((d) => d.md5),
          ),
        ],
      }));
    if (id === "ps2")
      files = db
        .filter((d) => whitelist.includes(d.md5))
        .map((d) => ({ path: d.name, md5: [d.md5] }));
    if (id === "gamecube")
      files = ["GC/USA/IPL.bin", "GC/EUR/IPL.bin", "GC/JAP/IPL.bin"].map((path) => ({
        path,
        md5: [
          ...new Set(
            db
              .filter((d) => d.name === "IPL.bin" && d.path.includes("/GameCube/"))
              .map((d) => d.md5),
          ),
        ],
      }));
    return { id, name, any, optional: ["nes", "n64", "gamecube", "psp"].includes(id), files };
  });
const aliases = { "bios7.bin": "NDS_Bios7.bin", "bios9.bin": "NDS_Bios9.bin" };
for (const system of result)
  for (const file of system.files) {
    const alias = aliases[file.path];
    if (alias) file.md5 = [...new Set(db.filter((d) => d.name === alias).map((d) => d.md5))];
  }
// Exact per-file hashes documented by Libretro, where retrobios stores the dump under
// another filename. These hashes also occur in the upstream EmuDeck system whitelist.
const exact = {
  "sega_101.bin": "85ec9ca47d8f6807718151cbcca8b964",
  "mpr-17933.bin": "3240872c70984b6cbfda1586cab68dbe",
  "dc/dc_boot.bin": "e10c53c2f8b90bab96ead2d368858623",
  "bios_CD_E.bin": "e66fa1dc5820d254611fdcdba0662372",
  "bios_CD_J.bin": "278a9397d192149e84e820ac621a8edd",
};
for (const system of result)
  for (const file of system.files) if (exact[file.path]) file.md5 = [exact[file.path]];
// Console key sets and firmware packages vary by console/version. Presence is not MD5 proof.
result
  .find((s) => s.id === "switch")
  .files.forEach((file) => {
    file.md5 = [];
  });
result.push({
  id: "ps3",
  name: "PlayStation 3",
  any: false,
  optional: false,
  files: [{ path: "PS3UPDAT.PUP", md5: [] }],
});
result
  .find((s) => s.id === "switch")
  .files.push(
    { path: "title.keys", md5: [], optional: true },
    { path: "switch/firmware", md5: [], directory: true },
  );
await mkdir("src/WSGM/Core/Emulators", { recursive: true });
await writeFile(
  "src/WSGM/Core/Emulators/bios-catalog.json",
  JSON.stringify(
    {
      source: "https://github.com/Abdess/retrobios/blob/" + commit.sha + "/platforms/emudeck.yml",
      revision: commit.sha,
      hashSources: [
        "https://github.com/Abdess/retrobios/blob/" + commit.sha + "/database.json",
        "https://docs.libretro.com/library/beetle_saturn/",
        "https://docs.libretro.com/library/flycast/",
        "https://docs.libretro.com/library/genesis_plus_gx/",
        "https://docs.libretro.com/library/melonds/",
      ],
      systems: result,
    },
    null,
    2,
  ) + "\n",
);
await writeFile(
  "src/WSGM/Core/Emulators/retrobios-LICENSE.txt",
  await (await fetch(root + "LICENSE")).text(),
);
console.log(
  JSON.stringify({
    revision: commit.sha,
    systems: result.length,
    files: result.reduce((n, s) => n + s.files.length, 0),
    unchecked: result.flatMap((s) =>
      s.files.filter((f) => !f.md5.length).map((f) => s.id + ":" + f.path),
    ),
  }),
);
