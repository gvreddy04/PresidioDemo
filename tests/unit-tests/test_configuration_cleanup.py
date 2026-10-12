"""Keep the reviewed binary and JSON policies aligned with supplied samples."""
import json
import unittest
import xml.etree.ElementTree as ET

from reference_feed_support import field_spans
from test_feed_listener import PROJECT
from test_seats_feed import payload


class ConfigurationCleanupTests(unittest.TestCase):
    def rules(self, feed):
        root = ET.parse(PROJECT / "src/config/feed-policies.xml").getroot()
        return {field.get("name"): field.attrib for entry in root if entry.get("type") == feed for field in entry}

    def test_binary_rules_cover_only_reviewed_spans_and_preserve_mask_lengths(self):
        for feed in ("ACI", "ETKT", "PNR"):
            with self.subTest(feed=feed):
                rules = self.rules(feed)
                spans = field_spans(feed)
                self.assertEqual(set(rules), {name for name, _, _ in spans})
                for name, _, length in spans:
                    rule = rules[name]
                    self.assertIn(rule["action"], ("Keep", "Mask"))
                    if rule["action"] == "Mask":
                        self.assertEqual(rule["identification"], "Known")
                        self.assertEqual(int(rule["maskCharacters"]), length)
                        self.assertEqual(rule["maskCharacter"], "*")
                    if name.split("/")[-1] == "pnr":
                        self.assertEqual(rule["action"], "Keep")
                self.assertEqual(sum(rule["action"] == "Mask" for rule in rules.values()), {"ACI": 6, "ETKT": 5, "PNR": 76}[feed])

    def json_sample_paths(self, filename):
        document = json.loads(payload((PROJECT / "docs/sample-docs" / filename).read_bytes()))
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

    def test_seats_rules_cover_exact_sample_paths_and_keep_pnr(self):
        paths = self.json_sample_paths("SEATS.txt")
        rules = self.rules("Seats")
        self.assertEqual(set(rules), paths)
        self.assertEqual(len(paths), 39)
        customer = "/OrderChangeNotif/Old/Customers/*/"
        self.assertEqual(rules[customer + "UARecloc"]["action"], "Keep")
        for name in ("PaxId", "FirstName", "LastName", "BookedAsName"):
            self.assertEqual(rules[customer + name]["action"], "Replace")
        self.assertEqual(rules["/OrderChangeNotif/EventDetail/EmpId"]["action"], "Replace")

    def test_pnr_has_only_reviewed_reference_paths_and_no_general_producer_schema(self):
        entry = ET.parse(PROJECT / "src/config/feed-policies.xml").find("feed[@type='PNR']")
        self.assertEqual(entry.get("configured"), "true")
        self.assertEqual(len(list(entry)), 106)
        self.assertEqual(self.rules("PNR")["/pnr"]["action"], "Keep")
        binary = ET.parse(PROJECT / "src/config/xdr-layouts.xml").find("feed[@type='PNR']")
        self.assertEqual(binary.get("configured"), "false")
        self.assertEqual(ET.parse(PROJECT / "src/config/reference-layouts.xml").find("feed[@type='PNR']").get("name"), "PnrReference5452")

    def test_pnr_linking_rules_cover_exact_sample_paths_and_preserve_pnr(self):
        rules = self.rules("PNR-Linking")
        self.assertEqual(set(rules), self.json_sample_paths("PNR_LINKING.txt"))
        self.assertEqual(len(rules), 49)
        self.assertEqual(rules["/*/PNROwner"]["action"], "Keep")
        self.assertEqual(rules["/*/Customers/*/UaRecordLocator"]["action"], "Keep")
        for name in ("PaxId", "OrderId", "FirstName", "LastName", "BookedAs", "DOB", "UaLoyaltyAcctNbr", "PartnerLoyaltyAcctNbr"):
            self.assertEqual(rules["/*/Customers/*/" + name]["action"], "Replace")
        self.assertEqual(rules["/*/transitionLinkSegments"]["action"], "Replace")


if __name__ == "__main__":
    unittest.main()
