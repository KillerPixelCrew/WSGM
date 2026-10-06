"""Generator regressions using disposable definitions and a small library-source fixture.

These exercise the real build script without PlatformIO, downloading dependencies or touching an
endpoint. The library fixture supplies only the protocol metadata these definitions need.
"""

import contextlib
import gzip
import io
import json
import re
import runpy
import tempfile
import unittest
from pathlib import Path


GENERATOR = Path(__file__).resolve().parents[1] / "embed_remotes.py"


class BuildEnvironment:
    def __init__(self, project):
        self.paths = {
            "$PROJECT_DIR": str(project),
            "$PROJECT_LIBDEPS_DIR": str(project / "libdeps"),
            "$PIOENV": "fixture",
            "$BUILD_DIR": str(project / "build"),
        }

    def subst(self, name):
        return self.paths[name]

    def Exit(self, code):
        raise SystemExit(code)

    def Append(self, **values):
        self.include_paths = values["CPPPATH"]


class EmbedRemotesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.project = Path(self.temporary.name)
        library = self.project / "libdeps" / "fixture" / "IRremoteESP8266" / "src"
        (library / "locale").mkdir(parents=True)
        (library / "locale" / "defaults.h").write_text(
            '#define D_STR_NEC "NEC"\n#define D_STR_MIDEA "MIDEA"\n', encoding="utf-8")
        (library / "IRtext.cpp").write_text(
            "IRTEXT_CONST_BLOB_DECL(kAllProtocolNamesStr) = {D_STR_UNUSED, D_STR_NEC, "
            "D_STR_MIDEA, D_STR_UNSUPPORTED};", encoding="utf-8")
        for filename, signature in (("IRac.cpp", "bool IRac::isProtocolSupported"),
                                    ("IRutils.cpp", "bool hasACState")):
            (library / filename).write_text(
                signature + "(decode_type_t protocol) { switch (protocol) {"
                "case decode_type_t::MIDEA: return true; } return false; }", encoding="utf-8")

    @staticmethod
    def definition():
        return {"name": "Fixture remote", "defaults": {"protocol": "NEC", "address": 4},
                "buttons": {"power": {"label": "Power", "command": 0x71}}}

    def write_remote(self, definition, root="remotes", page=None):
        folder = self.project / root / "fixture"
        folder.mkdir(parents=True, exist_ok=True)
        content = definition if isinstance(definition, str) else json.dumps(definition)
        (folder / "remote.json").write_text(content, encoding="utf-8")
        if page is not None:
            (folder / "index.html").write_text(page, encoding="utf-8")

    def generate(self, output=None):
        output = output if output is not None else io.StringIO()
        with contextlib.redirect_stdout(output):
            runpy.run_path(str(GENERATOR), init_globals={
                "Import": lambda name: None, "env": BuildEnvironment(self.project)
            })
        return output.getvalue()

    def generated_bytes(self, name):
        header = (self.project / "build" / "generated" / "remotes_generated.h").read_text(encoding="utf-8")
        array = re.search(re.escape(name) + r"\[\] = \{(.*?)\};", header, re.DOTALL)
        self.assertIsNotNone(array)
        return bytes(int(byte, 16) for byte in re.findall(r"0x([0-9a-f]{2})", array.group(1)))

    def generated_catalog(self):
        data = self.generated_bytes("CatalogJson")
        return data, json.loads(data)

    def test_large_catalog_and_generated_page_preserve_all_buttons_and_sequence_steps(self):
        definition = self.definition()
        definition["buttons"] = {
            f"button-{index}": {"label": f"Button {index} ✓", "command": index % 256}
            for index in range(7000)
        }
        definition["sequences"] = {
            "many": {"label": "Many steps", "steps": [{"button": "button-0"}] * 65}
        }
        self.write_remote(definition)

        self.generate()

        data, catalog = self.generated_catalog()
        self.assertGreater(len(data), 32768)
        remote = catalog["remotes"][0]
        self.assertEqual(len(remote["buttons"]), 7000)
        self.assertEqual(remote["buttons"][-1], {"id": "button-6999", "label": "Button 6999 ✓"})
        self.assertEqual(remote["sequences"], [{"id": "many", "label": "Many steps"}])
        definitions = json.loads(self.generated_bytes("RemotesJson"))
        self.assertEqual(len(definitions["remotes"][0]["sequences"][0]["steps"]), 65)
        self.assertGreater(len(gzip.decompress(self.generated_bytes("RemotePage0"))), 262144)

    def test_static_page_attributes_accept_case_quoting_and_entities(self):
        self.write_remote(self.definition(), page=
                          "<BUTTON DATA-BUTTON=power></BUTTON><button data-button='pow&#101;r'/>")

        self.generate()

        self.assertEqual(self.generated_catalog()[1]["remotes"][0]["buttons"][0]["id"], "power")

    def test_unknown_static_page_button_is_reported_with_its_definition(self):
        self.write_remote(self.definition(), page="<BUTTON DATA-BUTTON=missing>")
        output = io.StringIO()

        with self.assertRaises(SystemExit) as failure:
            self.generate(output)

        self.assertEqual(failure.exception.code, 1)
        self.assertIn("remotes/fixture/index.html", output.getvalue())
        self.assertIn("unknown button 'missing'", output.getvalue())

    def test_malformed_definitions_report_definition_errors(self):
        invalid = [
            {"name": "Remote", "buttons": {"power": None}},
            {"name": "Remote", "climate": {"protocol": "MIDEA", "modes": [["cool"]]}},
            {"name": "Remote", "defaults": {"protocol": "NEC"},
             "buttons": {"power": {"label": "Power", "value": "20DF8E71", "address": 4}}},
            '{"name":"Remote","buttons":{"power":{"label":"Power","label":"Other"}}}',
        ]
        for definition in invalid:
            with self.subTest(definition=definition):
                self.write_remote(definition)
                output = io.StringIO()
                with self.assertRaises(SystemExit) as failure:
                    self.generate(output)
                self.assertEqual(failure.exception.code, 1)
                self.assertIn("IR remotes: remotes/fixture", output.getvalue())

    def test_local_remote_deliberately_replaces_the_tracked_definition(self):
        self.write_remote(self.definition())
        replacement = self.definition()
        replacement["name"] = "Local replacement"
        self.write_remote(replacement, root="remotes.local")

        self.generate()

        self.assertEqual(self.generated_catalog()[1]["remotes"][0]["name"], "Local replacement")


if __name__ == "__main__":
    unittest.main()
