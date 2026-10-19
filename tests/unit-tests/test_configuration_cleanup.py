"""Keep the single master schema and shared catalog aligned with reviewed samples."""
import json
import unittest
import xml.etree.ElementTree as ET

from configuration_support import SCHEMA, CATALOG, REFERENCE, catalog, json_rules
from reference_feed_support import inspect_fields
from test_feed_listener import PROJECT, DLL
from test_seats_feed import payload


class ConfigurationCleanupTests(unittest.TestCase):
    def test_original_three_formats_with_optional_extensions(self):
        master = ET.parse(SCHEMA).getroot()
        self.assertEqual(master.tag, "xdrLayouts")
        self.assertEqual(len(master), 3)
        self.assertEqual({feed.get("type") for feed in master}, {"ACI", "ETKT", "PNR"})
        for node in master.iter():
            self.assertNotIn(node.tag, {"profile", "source", "fixed", "text"})
            self.assertTrue({"offset", "hex", "payloadBytes", "sourceSha256"}.isdisjoint(node.attrib))
            self.assertNotIn("policy", node.attrib)
        reference = ET.parse(REFERENCE).getroot()
        self.assertEqual(reference.tag, "referenceLayouts")
        for feed in reference:
            self.assertEqual(feed.get("schemaDriven"), "true")
            for text in feed:
                self.assertTrue({"name", "offset", "length"}.issubset(text.attrib))
        policies = ET.parse(CATALOG).getroot()
        self.assertEqual(policies.tag, "feedPolicies")
        self.assertEqual(len(policies.findall("feed")), 5)
        for name in ("feed-schemas.xml", "policy-catalog.xml"):
            self.assertFalse((SCHEMA.parent / name).exists())
        rules = catalog()
        self.assertGreater(len(rules), 0)
        for node in policies.iter():
            if "policy" in node.attrib:
                self.assertIn(node.get("policy"), rules)

    def test_binary_rules_cover_reviewed_values_without_masking_structural_words(self):
        rules = catalog()
        rules["KeepStructural"] = {"action": "Keep", "identification": "None"}
        for feed in ("ACI", "ETKT", "PNR"):
            rules.update({path: rule for path, rule in json_rules(feed).items() if "id" not in rule})
        count = 0
        for source in (PROJECT / "docs/sample-docs").glob("*.txt"):
            feed = "ACI" if source.stem.startswith("ACI") else "ETKT" if source.stem.startswith("ETKT") else "PNR" if source.stem == "PNR" else None
            if not feed:
                continue
            count += 1
            fields, _ = inspect_fields(feed, source.read_bytes())
            for path, _, _, policy, kind in fields:
                rule = rules[policy]
                if kind == "word" or path.rsplit("/", 1)[-1] == "pnr":
                    self.assertEqual(rule["action"], "Keep")
                if rule["action"] == "Mask":
                    self.assertEqual(kind, "text")
                    self.assertEqual(rule["identification"], "Known")
                    self.assertEqual(rule["maskCharacters"], "all")
                    self.assertEqual(len(rule["maskCharacter"].encode("ascii")), 1)
        self.assertEqual(count, 13)

    def json_paths(self, name):
        document = json.loads(payload((PROJECT / "docs/sample-docs" / name).read_bytes()))
        paths = set()
        def walk(value, path=""):
            if isinstance(value, dict):
                for key, item in value.items():
                    walk(item, path + "/" + key.replace("~", "~0").replace("/", "~1"))
            elif isinstance(value, list):
                for item in value:
                    walk(item, path + "/*")
            else:
                paths.add(path)
        walk(document)
        return paths

    def test_json_paths_and_pnr_preservation(self):
        for feed, sample, size in (("Seats", "SEATS.txt", 39), ("PNR-Linking", "PNR_LINKING.txt", 49)):
            rules = json_rules(feed)
            self.assertEqual(set(rules), self.json_paths(sample))
            self.assertEqual(len(rules), size)
            for path, rule in rules.items():
                if path.rsplit("/", 1)[-1].lower() in {"uarecloc", "uarecordlocator", "pnrowner"}:
                    self.assertEqual(rule["action"], "Keep")

    def test_missing_strings_and_optional_sections_are_structural(self):
        aci = ET.parse(SCHEMA).find("feed[@type='ACI']")
        self.assertEqual(len(aci.findall("optional")), 4)
        etkt = ET.parse(SCHEMA).find("feed[@type='ETKT']")
        word_count = sum(1 if node.tag == "uint32" else int(node.get("count")) if node.tag in {"fixedArray", "words"} else 0 for node in etkt)
        self.assertEqual(word_count, 110)
        self.assertEqual(len(etkt.findall("string")), 26)
        self.assertFalse(any(node.get("name") == "Text0132" for node in etkt.iter()))
        for name in ("ETKT.txt", "ETKT_05.txt", "ETKT_06.txt"):
            fields, _ = inspect_fields("ETKT", (PROJECT / "docs/sample-docs" / name).read_bytes())
            self.assertEqual([(start, length, kind) for _, start, length, _, kind in fields if start in (132, 136)],
                             [(132, 4, "word"), (136, 4, "word")])

    def test_invalid_catalog_is_rejected_before_input_is_claimed(self):
        import subprocess
        import tempfile
        from pathlib import Path
        (PROJECT / ".test-runs").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="invalid-catalog-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            docs = root / "docs"
            source = docs / "input-feeds/ACI/untouched.bin"
            source.parent.mkdir(parents=True)
            source.write_bytes(b"retain-before-validation")
            tree = ET.parse(CATALOG)
            rule = tree.find("catalog")[0]
            rule.tag = "field"  # Legacy field syntax is not a shared catalog entry.
            rule.set("name", rule.attrib.pop("id"))
            invalid = root / "invalid.xml"
            tree.write(invalid, encoding="utf-8", xml_declaration=True)
            result = subprocess.run(["dotnet", str(DLL), "--docs-root", str(docs), "--policy-file", str(invalid)],
                                    cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", timeout=30)
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertNotIn("Initializing embedded", result.stdout)
            self.assertEqual(source.read_bytes(), b"retain-before-validation")
            self.assertFalse((docs / "feed-processing").exists())


if __name__ == "__main__":
    unittest.main()
