"""Embeds IR remote definitions into the endpoint firmware at build time.

Each remote is a folder holding remote.json and an optional index.html. Tracked examples live in
remotes/, a maintainer's own remotes in the untracked remotes.local/, which wins on the same id.
Definitions are checked here against the pinned IRremoteESP8266 sources, so a broken remote fails
the build instead of failing on the endpoint. The format is described in ../README.md.
"""

import gzip
import html
import json
import re
from pathlib import Path

Import("env")

PROJECT = Path(env.subst("$PROJECT_DIR"))
LIBRARY = Path(env.subst("$PROJECT_LIBDEPS_DIR"), env.subst("$PIOENV"), "IRremoteESP8266", "src")
GENERATED = Path(env.subst("$BUILD_DIR"), "generated")

ID = re.compile(r"[a-z0-9][a-z0-9-]{0,31}")
MODES = {"auto", "cool", "heat", "dry", "fan", "off"}
FANS = {"auto", "min", "low", "medium", "high", "max"}
SWING_V = {"off", "auto", "min", "low", "middle", "high", "max"}
SWING_H = {"off", "auto", "leftmax", "left", "middle", "right", "rightmax"}
AC_FIELDS = {"protocol", "model", "power", "mode", "degrees", "celsius", "fan", "swingV", "swingH",
             "quiet", "turbo", "econo", "light", "filter", "clean", "beep", "sleep"}
CODE_FIELDS = {"protocol", "value", "address", "command", "state", "bits", "repeats"}


class DefinitionError(Exception):
    pass


def fail(where, message):
    raise DefinitionError(f"{where}: {message}")


def switch_cases(path, signature):
    source = path.read_text(encoding="utf-8")
    start = source.index(signature)
    body = source[start:source.index("return true;", start)]
    return {name.upper() for name in re.findall(r"case decode_type_t::(\w+):", body)}


class Library:
    """Protocol names and capabilities as the pinned library defines them."""

    def __init__(self):
        if not LIBRARY.is_dir():
            raise DefinitionError(f"IRremoteESP8266 sources not found at {LIBRARY}")
        defaults = (LIBRARY / "locale" / "defaults.h").read_text(encoding="utf-8")
        defines = dict(re.findall(r"#define\s+(D_STR_\w+)\s+([^\n]+)", defaults))

        def resolve(macro):
            if macro not in defines:
                raise DefinitionError(f"cannot resolve {macro} in the IRremoteESP8266 sources")
            parts = re.findall(r'"([^"]*)"|(D_STR_\w+)', defines[macro])
            return "".join(resolve(name) if name else literal for literal, name in parts)

        source = (LIBRARY / "IRtext.cpp").read_text(encoding="utf-8")
        start = source.index("IRTEXT_CONST_BLOB_DECL(kAllProtocolNamesStr)")
        blob = source[start:source.index("};", start)]
        names = [resolve(macro) for macro in re.findall(r"D_STR_\w+", blob)
                 if macro not in ("D_STR_UNUSED", "D_STR_UNSUPPORTED")]
        self.names = {name.upper(): name for name in names}
        self.ac = switch_cases(LIBRARY / "IRac.cpp", "bool IRac::isProtocolSupported")
        self.state = switch_cases(LIBRARY / "IRutils.cpp", "bool hasACState")

    def protocol(self, where, value, capability=None):
        if not isinstance(value, str) or value.upper() not in self.names:
            fail(where, f"unknown IRremoteESP8266 protocol {value!r}")
        if capability is not None and value.upper() not in capability:
            fail(where, f"{value} has no A/C support in IRremoteESP8266")
        return self.names[value.upper()]


def text(where, value, limit):
    if not isinstance(value, str) or not value.strip() or len(value) > limit:
        fail(where, f"must be a non-empty string of at most {limit} characters")
    return value


def number(where, value, low, high, integral=True):
    if isinstance(value, str) and re.fullmatch(r"0[xX][0-9a-fA-F]+", value):
        value = int(value, 16)
    kinds = (int,) if integral else (int, float)
    if isinstance(value, bool) or not isinstance(value, kinds) or not low <= value <= high:
        fail(where, f"must be {'an integer' if integral else 'a number'} from {low} to {high}")
    return value


def flag(where, value):
    if not isinstance(value, bool):
        fail(where, "must be true or false")
    return value


def choice(where, value, allowed):
    if value not in allowed:
        fail(where, "must be one of " + ", ".join(sorted(allowed)))
    return value


def fields(where, value, allowed):
    if not isinstance(value, dict):
        fail(where, "must be an object")
    unknown = sorted(set(value) - set(allowed))
    if unknown:
        fail(where, "unknown field " + ", ".join(unknown))
    return value


def entries(where, value):
    if not isinstance(value, dict):
        fail(where, "must be an object keyed by id")
    for key in value:
        if not ID.fullmatch(key):
            fail(where, f"{key!r} is not a lowercase id of letters, digits and dashes")
    return value.items()


def reverse(value, bits):
    return int(f"{value:0{bits}b}"[::-1], 2)


def encode_nec(address, command):
    # Mirrors IRsend::encodeNEC: bytes go out LSB first, and a 16-bit address is extended NEC.
    command = reverse(command, 8)
    command = (command << 8) | (command ^ 0xFF)
    if address > 0xFF:
        return (reverse(address, 16) << 16) | command
    address = reverse(address, 8)
    return (address << 24) | ((address ^ 0xFF) << 16) | command


def code(where, source, library):
    protocol = library.protocol(f"{where}.protocol", source.get("protocol"))
    result = {"protocol": protocol}
    if protocol.upper() in library.state:
        if set(source) & {"value", "address", "command", "bits", "repeats"}:
            fail(where, f"{protocol} carries a byte state; give only state")
        state = source.get("state")
        if not isinstance(state, str) or not re.fullmatch(r"(?:[0-9a-fA-F]{2}){1,64}", state):
            fail(f"{where}.state", "must be 2 to 128 hex digits")
        result["state"] = state.upper()
        return result
    if "state" in source:
        fail(where, f"{protocol} takes value, not state")
    if "command" in source:
        if protocol != "NEC" or "value" in source:
            fail(where, "address and command are only for NEC, instead of value")
        address = number(f"{where}.address", source.get("address"), 0, 0xFFFF)
        command = number(f"{where}.command", source["command"], 0, 0xFF)
        result["value"] = f"{encode_nec(address, command):08X}"
    else:
        value = source.get("value")
        if not isinstance(value, str) or not re.fullmatch(r"(?:0[xX])?[0-9a-fA-F]{1,16}", value):
            fail(f"{where}.value", "must be a hex string of at most 64 bits")
        result["value"] = re.sub(r"^0[xX]", "", value).upper()
    if "bits" in source:
        result["bits"] = number(f"{where}.bits", source["bits"], 1, 64)
    if "repeats" in source:
        result["repeats"] = number(f"{where}.repeats", source["repeats"], 0, 4)
    return result


def ac_state(where, source, library):
    fields(where, source, AC_FIELDS)
    state = {"protocol": library.protocol(f"{where}.protocol", source.get("protocol"), library.ac)}
    for key, value in source.items():
        at = f"{where}.{key}"
        if key == "protocol":
            continue
        if key == "model":
            if not (isinstance(value, str) and value.strip()):
                value = number(at, value, -1, 32767)
        elif key == "mode":
            choice(at, value, MODES)
        elif key == "fan":
            choice(at, value, FANS)
        elif key == "swingV":
            choice(at, value, SWING_V)
        elif key == "swingH":
            choice(at, value, SWING_H)
        elif key == "degrees":
            value = number(at, value, 10, 90, integral=False)
        elif key == "sleep":
            value = number(at, value, -1, 32767)
        else:
            flag(at, value)
        state[key] = value
    return state


def raw(where, source):
    payload = fields(f"{where}.raw", source["raw"], {"carrierHz", "timingsUs"})
    carrier = number(f"{where}.raw.carrierHz", payload.get("carrierHz"), 20000, 60000)
    timings = payload.get("timingsUs")
    if not isinstance(timings, list) or not 2 <= len(timings) <= 1024:
        fail(f"{where}.raw.timingsUs", "must list 2 to 1024 timings")
    timings = [number(f"{where}.raw.timingsUs[{i}]", t, 1, 65535) for i, t in enumerate(timings)]
    repeats = number(f"{where}.repeats", source.get("repeats", 0), 0, 4)
    gap = number(f"{where}.gapMs", source.get("gapMs", 40), 0, 200)
    duration = sum(timings)
    if duration > 2_000_000 or duration * (repeats + 1) + gap * 1000 * repeats > 5_000_000:
        fail(where, "exceeds the endpoint's transmission duration limit")
    return {"raw": {"carrierHz": carrier, "timingsUs": timings}, "repeats": repeats, "gapMs": gap}


def button(where, source, defaults, library):
    if not isinstance(source, dict):
        fail(where, "must be an object")
    if "ac" in source and "raw" in source:
        fail(where, "give only one of ac or raw")
    if "ac" in source:
        fields(where, source, {"label", "ac"})
        body = {"ac": ac_state(f"{where}.ac", source["ac"], library)}
    elif "raw" in source:
        fields(where, source, {"label", "raw", "repeats", "gapMs"})
        body = raw(where, source)
    else:
        fields(where, source, {"label"} | CODE_FIELDS)
        merged = dict(defaults)
        if "command" not in source:
            merged.pop("address", None)
        merged.update(source)
        merged.pop("label")
        body = {"code": code(where, merged, library)}
    return {"label": text(f"{where}.label", source.get("label"), 48), **body}


def climate(where, source, library):
    fields(where, source, {"protocol", "model", "celsius", "modes", "fans", "minDegrees",
                           "maxDegrees", "swing"})
    result = {"protocol": library.protocol(f"{where}.protocol", source.get("protocol"), library.ac)}
    if "model" in source:
        model = source["model"]
        result["model"] = model if isinstance(model, str) and model.strip() else number(
            f"{where}.model", model, -1, 32767)
    for key, allowed in (("modes", MODES - {"off"}), ("fans", FANS)):
        values = source.get(key)
        if not isinstance(values, list) or not values or len(set(values)) != len(values):
            fail(f"{where}.{key}", "must list distinct names")
        for index, value in enumerate(values):
            choice(f"{where}.{key}[{index}]", value, allowed)
        result[key] = values
    low = number(f"{where}.minDegrees", source.get("minDegrees"), 10, 90, integral=False)
    high = number(f"{where}.maxDegrees", source.get("maxDegrees"), 10, 90, integral=False)
    if low >= high:
        fail(where, "minDegrees must be below maxDegrees")
    result["minDegrees"] = low
    result["maxDegrees"] = high
    result["celsius"] = flag(f"{where}.celsius", source.get("celsius", True))
    result["swing"] = choice(f"{where}.swing", source.get("swing", "none"), {"none", "toggle"})
    return result


def sequence(where, source, buttons):
    fields(where, source, {"label", "steps"})
    steps = source.get("steps")
    if not isinstance(steps, list) or not 1 <= len(steps) <= 32:
        fail(f"{where}.steps", "must list 1 to 32 steps")
    result, total = [], 0
    for index, step in enumerate(steps):
        at = f"{where}.steps[{index}]"
        if isinstance(step, dict) and set(step) == {"button"}:
            if step["button"] not in buttons:
                fail(at, f"unknown button {step['button']!r}")
            result.append({"button": step["button"]})
        elif isinstance(step, dict) and set(step) == {"delayMs"}:
            delay = number(f"{at}.delayMs", step["delayMs"], 1, 600_000)
            total += delay
            result.append({"delayMs": delay})
        else:
            fail(at, "must be exactly one of button or delayMs")
    if total > 600_000:
        fail(where, "delays exceed ten minutes in total")
    return {"label": text(f"{where}.label", source.get("label"), 48), "steps": result}


STYLE = """
:root{color-scheme:light dark;font-family:system-ui,sans-serif}
body{margin:0;background:Canvas;color:CanvasText}
main{max-width:34rem;margin:0 auto;padding:1rem}
h1{font-size:1.4rem;margin:.25rem 0 1rem}h2{font-size:1rem;margin:1.25rem 0 .5rem;opacity:.7}
a{color:inherit}ul{padding:0;list-style:none}li{margin:.5rem 0}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(7.5rem,1fr));gap:.5rem}
button{font:inherit;padding:.8rem .5rem;border-radius:.6rem;color:inherit;cursor:pointer;
border:1px solid color-mix(in srgb,CanvasText 25%,transparent);
background:color-mix(in srgb,CanvasText 6%,Canvas);touch-action:manipulation}
button:active{background:color-mix(in srgb,CanvasText 16%,Canvas)}
button[aria-pressed=true]{outline:2px solid AccentColor}
.degrees{font-size:2rem;min-width:6rem;text-align:center}
.stepper{display:flex;gap:.5rem;align-items:center;justify-content:center}
#status{min-height:1.5em;opacity:.8}
"""

SCRIPT = """
const statusLine = document.getElementById("status");
async function post(path, body) {
  statusLine.textContent = "Sending...";
  try {
    const response = await fetch(path, {method: "POST", body: body && JSON.stringify(body),
      headers: {"X-WSGM-IR": "1", "Content-Type": "application/json"}});
    const result = await response.json();
    statusLine.textContent = result.status === "transmitted" ? "" :
      result.status === "started" ? "Sequence running" : result.status;
    return result.status;
  } catch (error) {
    statusLine.textContent = "Endpoint unreachable";
  }
}
document.querySelectorAll("[data-button]").forEach(key =>
  key.addEventListener("click", () => post("buttons/" + key.dataset.button)));
document.querySelectorAll("[data-sequence]").forEach(key =>
  key.addEventListener("click", () => post("sequences/" + key.dataset.sequence)));
const climate = document.querySelector("[data-climate]");
if (climate) {
  const config = JSON.parse(climate.dataset.climate);
  const storageKey = "wsgm-ir-climate:" + location.pathname;
  let state = {power: false, mode: config.modes[0], fan: config.fans[0],
    degrees: Math.round((config.minDegrees + config.maxDegrees) / 2)};
  try { Object.assign(state, JSON.parse(localStorage.getItem(storageKey))); } catch (error) {}
  const render = () => {
    climate.querySelector(".degrees").textContent = state.degrees + (config.celsius ? " \\u00b0C" : " \\u00b0F");
    climate.querySelectorAll("[data-mode]").forEach(k => k.setAttribute("aria-pressed", k.dataset.mode === state.mode));
    climate.querySelectorAll("[data-fan]").forEach(k => k.setAttribute("aria-pressed", k.dataset.fan === state.fan));
    climate.querySelector("[data-power=on]").setAttribute("aria-pressed", state.power);
    climate.querySelector("[data-power=off]").setAttribute("aria-pressed", !state.power);
  };
  // IR is one-way, so this page remembers what it last sent; the unit's own panel can differ.
  const send = async (change, toggleSwing) => {
    const next = Object.assign({}, state, change);
    const body = Object.assign({}, next, toggleSwing ? {toggleSwing: true} : {});
    if (await post("climate", body) === "transmitted") {
      state = next;
      try { localStorage.setItem(storageKey, JSON.stringify(state)); } catch (error) {}
      render();
    }
  };
  const clamp = value => Math.min(config.maxDegrees, Math.max(config.minDegrees, value));
  climate.querySelector("[data-power=on]").onclick = () => send({power: true});
  climate.querySelector("[data-power=off]").onclick = () => send({power: false});
  climate.querySelector("[data-step=down]").onclick = () => send({power: true, degrees: clamp(state.degrees - 1)});
  climate.querySelector("[data-step=up]").onclick = () => send({power: true, degrees: clamp(state.degrees + 1)});
  climate.querySelectorAll("[data-mode]").forEach(k => k.onclick = () => send({power: true, mode: k.dataset.mode}));
  climate.querySelectorAll("[data-fan]").forEach(k => k.onclick = () => send({power: true, fan: k.dataset.fan}));
  const swing = climate.querySelector("[data-swing]");
  if (swing) swing.onclick = () => send({power: true}, true);
  render();
}
"""


def document(title, sections, back):
    link = '<p><a href="../../">All remotes</a></p>' if back else ""
    return ("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            f"<title>{html.escape(title)}</title><style>{STYLE}</style></head><body><main>{link}"
            f"<h1>{html.escape(title)}</h1>{sections}<p id=\"status\" role=\"status\"></p></main>"
            f"<script>{SCRIPT}</script></body></html>")


def keys(attribute, items):
    return "".join(f'<button {attribute}="{html.escape(item["id"])}">{html.escape(item["label"])}</button>'
                   for item in items)


def default_page(entry):
    sections = ""
    if "climate" in entry:
        config = entry["climate"]
        swing = '<h2>Swing</h2><div class="grid"><button data-swing>Toggle swing</button></div>' \
            if config["swing"] == "toggle" else ""
        sections += (
            f'<section data-climate="{html.escape(json.dumps(config))}"><h2>Climate</h2>'
            '<div class="grid"><button data-power="on">On</button><button data-power="off">Off</button></div>'
            '<h2>Temperature</h2><div class="stepper"><button data-step="down">-</button>'
            '<span class="degrees"></span><button data-step="up">+</button></div>'
            '<h2>Mode</h2><div class="grid">'
            + "".join(f'<button data-mode="{m}">{m.capitalize()}</button>' for m in config["modes"])
            + '</div><h2>Fan</h2><div class="grid">'
            + "".join(f'<button data-fan="{f}">{f.capitalize()}</button>' for f in config["fans"])
            + f"</div>{swing}</section>")
    if entry["buttons"]:
        sections += f'<h2>Buttons</h2><div class="grid">{keys("data-button", entry["buttons"])}</div>'
    if entry["sequences"]:
        sections += f'<h2>Sequences</h2><div class="grid">{keys("data-sequence", entry["sequences"])}</div>'
    return document(entry["name"], sections, back=True)


def index_page(entries_list):
    items = "".join(f'<li><a href="remotes/{e["id"]}/">{html.escape(e["name"])}</a></li>'
                    for e in entries_list)
    return document("IR remotes", f"<ul>{items or '<li>No remotes are built in.</li>'}</ul>", back=False)


def check_page_references(where, page, entry):
    ids = {"data-button": {b["id"] for b in entry["buttons"]},
           "data-sequence": {s["id"] for s in entry["sequences"]}}
    for attribute, known in ids.items():
        for name in re.findall(attribute + r'="([^"]+)"', page):
            if name not in known:
                fail(where, f"index.html references unknown {attribute[5:]} {name!r}")


def load(identifier, folder, library):
    where = folder.relative_to(PROJECT).as_posix()
    try:
        source = json.loads((folder / "remote.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        fail(where, f"remote.json is not readable JSON ({error})")
    fields(where, source, {"name", "defaults", "buttons", "climate", "sequences"})
    defaults = fields(f"{where}.defaults", source.get("defaults", {}),
                      {"protocol", "address", "bits", "repeats"})
    entry = {"id": identifier, "name": text(f"{where}.name", source.get("name"), 64)}
    entry["buttons"] = [{"id": key, **button(f"{where}.buttons.{key}", value, defaults, library)}
                        for key, value in entries(f"{where}.buttons", source.get("buttons", {}))]
    if len(entry["buttons"]) > 128:
        fail(where, "has more than 128 buttons")
    if "climate" in source:
        entry["climate"] = climate(f"{where}.climate", source["climate"], library)
    if not entry["buttons"] and "climate" not in entry:
        fail(where, "needs buttons or climate")
    names = {b["id"] for b in entry["buttons"]}
    entry["sequences"] = [{"id": key, **sequence(f"{where}.sequences.{key}", value, names)}
                          for key, value in entries(f"{where}.sequences", source.get("sequences", {}))]
    custom = folder / "index.html"
    if custom.is_file():
        page = custom.read_text(encoding="utf-8")
        check_page_references(f"{where}/index.html", page, entry)
    else:
        page = default_page(entry)
    if len(page.encode("utf-8")) > 262_144:
        fail(where, "index.html is larger than 256 KiB")
    return entry, page


def summary(entry):
    result = {"id": entry["id"], "name": entry["name"],
              "buttons": [{"id": b["id"], "label": b["label"]} for b in entry["buttons"]],
              "sequences": [{"id": s["id"], "label": s["label"]} for s in entry["sequences"]]}
    if "climate" in entry:
        result["climate"] = entry["climate"]
    return result


def c_array(name, data):
    rows = ",\n".join("  " + ", ".join(f"0x{byte:02x}" for byte in data[i:i + 16])
                      for i in range(0, len(data), 16))
    return f"static const uint8_t {name}[] = {{\n{rows}\n}};\n"


def compress(text_value):
    return gzip.compress(text_value.encode("utf-8"), compresslevel=9, mtime=0)


def generate():
    library = Library()
    folders = {}
    for root in ("remotes", "remotes.local"):
        base = PROJECT / root
        if not base.is_dir():
            continue
        for folder in sorted(path for path in base.iterdir() if path.is_dir()):
            where = folder.relative_to(PROJECT).as_posix()
            if not ID.fullmatch(folder.name):
                fail(where, "folder name must be a lowercase id of letters, digits and dashes")
            if not (folder / "remote.json").is_file():
                fail(where, "missing remote.json")
            folders[folder.name] = folder
    loaded = [load(identifier, folders[identifier], library) for identifier in sorted(folders)]
    remotes = [entry for entry, _ in loaded]
    pages = [compress(page) for _, page in loaded]
    compact = {"separators": (",", ":"), "ensure_ascii": False}
    parts = [
        "// Generated by embed_remotes.py from remotes/ and remotes.local/. Do not edit.\n",
        "#pragma once\n#include <cstddef>\n#include <cstdint>\n\n",
        "struct EmbeddedPage {\n  const char *remote;\n  const uint8_t *gzip;\n  size_t length;\n};\n\n",
        c_array("RemotesJson", json.dumps({"remotes": remotes}, **compact).encode("utf-8")),
        c_array("CatalogJson", json.dumps({"remotes": [summary(e) for e in remotes]}, **compact)
                .encode("utf-8")),
        c_array("IndexPage", compress(index_page(remotes))),
    ]
    parts += [c_array(f"RemotePage{i}", page) for i, page in enumerate(pages)]
    rows = [f'  {{"{e["id"]}", RemotePage{i}, sizeof(RemotePage{i})}},' for i, e in enumerate(remotes)]
    parts.append("static const EmbeddedPage RemotePages[] = {\n"
                 + ("\n".join(rows) if rows else '  {"", IndexPage, 0},') + "\n};\n")
    parts.append(f"static const size_t RemotePageCount = {len(remotes)};\n")
    content = "".join(parts)
    GENERATED.mkdir(parents=True, exist_ok=True)
    target = GENERATED / "remotes_generated.h"
    if not target.is_file() or target.read_text(encoding="utf-8") != content:
        target.write_text(content, encoding="utf-8")
    described = ", ".join(f"{e['id']} ({folders[e['id']].parent.name})" for e in remotes) or "none"
    print(f"IR remotes: {described}; {sum(len(p) for p in pages)} bytes of compressed pages")


try:
    generate()
except DefinitionError as error:
    print(f"IR remotes: {error}")
    env.Exit(1)

env.Append(CPPPATH=[str(GENERATED)])
