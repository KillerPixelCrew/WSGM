// Apply the Steam compatibility policy in memory; never rewrite the installed MCP dependency.
let dependencyUrl;

export function initialize(data) {
  dependencyUrl = data.dependencyUrl;
}

export async function load(url, context, nextLoad) {
  const loaded = await nextLoad(url, context);
  if (url !== dependencyUrl) return loaded;

  const source = String(loaded.source);
  const marker = "waitForDebuggerOnStart: true,";
  if (source.split(marker).length - 1 !== 3) {
    throw new Error(
      "Steam MCP auto-attach layout changed. Review the pinned dependency before connecting.",
    );
  }
  return { ...loaded, source: source.replaceAll(marker, "waitForDebuggerOnStart: false,") };
}
