"""All-route CLI checks. Synthetic success fixtures are explicitly not producer schemas."""
from pathlib import Path
import hashlib
import json
import shutil
import struct
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

from test_feed_listener import PROJECT, DLL, runtime_root
from test_seats_feed import payload, verify_output
from reference_feed_support import verify_output as verify_reference_output


class AllFeedBatchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (PROJECT / ".test-runs").mkdir(exist_ok=True)

    def run_batch(self, sources, docs, extra=()):
        process = subprocess.run(["dotnet", str(DLL), "--docs-root", str(docs), "--process-folder", str(sources), *extra],
                                 cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", timeout=120)
        reports = list((runtime_root(docs) / "reports").glob("feed-batch-*.json"))
        self.assertEqual(len(reports), 1, process.stdout + process.stderr)
        return process, json.loads(reports[0].read_text(encoding="utf-8"))

    def verify_transaction_logs(self, docs, report):
        logs = list((docs / "feed-logs").glob("*.txt"))
        self.assertEqual(len(logs), len(report["Items"]))
        self.assertFalse(list((docs / "feed-logs").glob("*.pending")))
        self.assertFalse(list((docs / "feed-processing").rglob("*.txt")))
        self.assertFalse(list((docs / "feed-processing").rglob("*.bin")))
        for item in report["Items"]:
            if item["Status"] == "Published":
                transaction = Path(item["OutputFile"]).stem.split(".")[-1]
            else:
                transaction = Path(item["RetainedFile"]).stem.split(".")[-1]
            log = docs / "feed-logs" / (item["Feed"] + "." + transaction + ".txt")
            text = log.read_text(encoding="utf-8")
            self.assertEqual(text.count("BEFORE ANONYMIZATION"), 1)
            self.assertEqual(text.count("AFTER ANONYMIZATION"), 1)
            before, after = text.split("AFTER ANONYMIZATION")
            self.assertIn("Complete feed data", before)
            if item["Status"] == "Published":
                self.assertIn("Parsed fields", before)
                self.assertIn("Parsed fields", after)
                self.assertIn("Complete feed data", after)
            else:
                self.assertIn("No validated anonymized output was produced.", after)
                self.assertIn("Reason:", after)
            source = Path(item["SourceFile"])
            if source.suffix == ".txt":
                self.assertIn(source.read_text(encoding="utf-8"), before)
            if item["Status"] == "Published" and source.name in ("ACI.bin", "PNR.bin", "TKT.bin"):
                self.assertIn("Emily Carter", before)
                self.assertIn("Michael Brooks", before)
                self.assertNotIn("Emily Carter", after)
                self.assertNotIn("emily.carter@example.com", after)
                self.assertIn("[PASSENGER_NAME]", after)

    def test_actual_supplied_files_report_every_outcome_and_preserve_sources(self):
        with tempfile.TemporaryDirectory(prefix="actual-batch-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            sources = root / "sources"
            shutil.copytree(PROJECT / "docs" / "sample-docs", sources)
            before = {p.name: p.read_bytes() for p in sources.iterdir()}
            process, report = self.run_batch(sources, root / "docs")
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            self.assertTrue(report["AllPublished"])
            self.assertEqual(len(report["Items"]), 5)
            self.verify_transaction_logs(root / "docs", report)
            statuses = {item["Feed"]: item["Status"] for item in report["Items"]}
            self.assertEqual(statuses, {"Seats": "Published", "ACI": "Published", "ETKT": "Published", "PNR": "Published", "PNR-Linking": "Published"})
            for item in report["Items"]:
                original = before[Path(item["SourceFile"]).name]
                self.assertEqual(Path(item["SourceFile"]).read_bytes(), original)
                self.assertEqual(item["SourceSha256"], hashlib.sha256(original).hexdigest())
                if item["Status"] == "Published":
                    output = Path(item["OutputFile"]).read_bytes()
                    if item["Feed"] in ("ACI", "ETKT", "PNR"):
                        expected = {"ACI": {"Keep": 17, "Mask": 6}, "ETKT": {"Keep": 4, "Mask": 5}, "PNR": {"Keep": 30, "Mask": 76}}[item["Feed"]]
                        self.assertEqual(verify_reference_output(item["Feed"], original, output), expected)
                    else:
                        result = verify_output(original, output, item["Feed"])
                        expected = {"Keep": 30, "Replace": 29} if item["Feed"] == "Seats" else {"Keep": 54, "Replace": 22}
                        self.assertEqual(result["actions"], expected)
                else:
                    self.assertIsNone(item["OutputFile"])
                    self.assertEqual(Path(item["RetainedFile"]).read_bytes(), original)
                    self.assertTrue(Path(item["ErrorReport"]).exists())


    def test_supplied_test_only_pnr_layout_cannot_enable_an_unconfigured_policy(self):
        with tempfile.TemporaryDirectory(prefix="pnr-policy-guard-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            sources = root / "test-only-sources"
            sources.mkdir()
            raw = struct.pack(">I", 2)
            (sources / "PNR.bin").write_bytes(raw)
            layouts = ET.Element("xdrLayouts")
            for feed in ("ACI", "PNR", "ETKT"):
                entry = ET.SubElement(layouts, "feed", type=feed, configured="true" if feed == "PNR" else "false")
                if feed == "PNR":
                    ET.SubElement(entry, "uint32", name="Version", expected="2")
            layoutfile = root / "test-only-xdr.xml"
            ET.ElementTree(layouts).write(layoutfile, encoding="utf-8", xml_declaration=True)
            policies = ET.parse(PROJECT / "src/config/feed-policies.xml")
            pending = policies.find("feed[@type='PNR']")
            for field in list(pending):
                pending.remove(field)
            pending.set("configured", "false")
            policyfile = root / "test-only-unconfigured-policy.xml"
            policies.write(policyfile, encoding="utf-8", xml_declaration=True)
            referencefile = root / "test-only-no-reference-layouts.xml"
            referencefile.write_text("<referenceLayouts />", encoding="utf-8")
            process, report = self.run_batch(sources, root / "docs", ["--xdr-layout-file", str(layoutfile), "--policy-file", str(policyfile), "--reference-layout-file", str(referencefile)])
            self.assertEqual(process.returncode, 2, process.stdout + process.stderr)
            self.assertFalse(report["AllPublished"])
            self.assertEqual(len(report["Items"]), 1)
            item = report["Items"][0]
            self.assertEqual(item["Status"], "Rejected")
            self.assertIsNone(item["OutputFile"])
            self.assertEqual(Path(item["RetainedFile"]).read_bytes(), raw)
            self.assertIn("feed protection policy is not configured", Path(item["ErrorReport"]).read_text(encoding="utf-8"))
            self.verify_transaction_logs(root / "docs", report)

    def test_all_five_routes_publish_with_explicit_test_only_layouts_and_complete_json(self):
        with tempfile.TemporaryDirectory(prefix="test-only-five-routes-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            sources = root / "test-only-sources"
            sources.mkdir()
            def u32(value): return struct.pack('>I', value)
            def string(value):
                encoded = value.encode('ascii')
                return u32(len(encoded)) + encoded + bytes((-len(encoded)) % 4)
            passengers = [("Emily Carter", "PAX-1001", "emily.carter@example.com"), ("Michael Brooks", "PAX-1002", "michael.brooks@example.com")]
            binary = u32(2) + string("K7QX2M") + u32(2) + b''.join(string(value) for passenger in passengers for value in passenger)
            for name in ["ACI.bin", "PNR.bin", "TKT.bin"]:
                (sources / name).write_bytes(binary)
            (sources / "SEATS.txt").write_bytes((PROJECT / "tests" / "unit-tests" / "fixtures" / "seats-mq-export.txt").read_bytes())
            linking = [{"PNROwner": "K7QX2M", "Customers": [{"PaxId": p[1], "FirstName": p[0].split()[0], "LastName": p[0].split()[1], "UaRecordLocator": "K7QX2M", "DOB": None} for p in passengers]}]
            (sources / "PNR_LINKING.bin").write_bytes(json.dumps(linking).encode())
            layout = ET.Element('xdrLayouts')
            policies = ET.parse(PROJECT / "src" / "config" / "feed-policies.xml")
            for feed in ['ACI', 'PNR', 'ETKT']:
                entry = ET.SubElement(layout, 'feed', type=feed, configured='true')
                ET.SubElement(entry, 'uint32', name='Version', expected='2')
                ET.SubElement(entry, 'string', name='pnr', max='6')
                array = ET.SubElement(entry, 'array', name='Passengers', max='10')
                item = ET.SubElement(array, 'struct', name='Item')
                for name in ['Name', 'PassengerId', 'Email']: ET.SubElement(item, 'string', name=name, max='128')
                policy = next(e for e in policies.getroot() if e.get('type') == feed)
                policy.set('configured', 'true')
                for existing in list(policy):
                    policy.remove(existing)
                for path in ['/Version', '/pnr']:
                    if not any(field.get('name') == path for field in policy):
                        ET.SubElement(policy, 'field', name=path, identification='None', action='Keep')
                for name,entity,replacement in [('Name','PERSON','[PASSENGER_NAME]'),('PassengerId','PASSENGER_ID','[PASSENGER_ID]'),('Email','EMAIL_ADDRESS','[EMAIL_ADDRESS]')]:
                    ET.SubElement(policy, 'field', name='/Passengers/*/'+name, identification='Known', action='Replace', entity=entity, replacement=replacement)
            layoutfile = root / 'test-only-xdr-layouts.xml'
            ET.ElementTree(layout).write(layoutfile, encoding='utf-8', xml_declaration=True)
            policyfile = root / 'test-only-policies.xml'
            policies.write(policyfile, encoding='utf-8', xml_declaration=True)
            before = {p.name:p.read_bytes() for p in sources.iterdir()}
            referencefile = root / "test-only-no-reference-layouts.xml"
            referencefile.write_text("<referenceLayouts />", encoding="utf-8")
            process,report = self.run_batch(sources, root / 'docs', ['--xdr-layout-file',str(layoutfile),'--policy-file',str(policyfile),'--reference-layout-file',str(referencefile)])
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            self.assertTrue(report['AllPublished'])
            self.assertEqual(len(report['Items']), 5)
            self.verify_transaction_logs(root / 'docs', report)
            for item in report['Items']:
                self.assertEqual(item['Status'], 'Published')
                self.assertEqual(Path(item['SourceFile']).read_bytes(), before[Path(item['SourceFile']).name])
                data = Path(item['OutputFile']).read_bytes()
                if item['Feed'] == 'Seats':
                    verify_output(before['SEATS.txt'],data)
                else:
                    for original in ['Emily','Carter','Michael','Brooks','PAX-1001','PAX-1002','emily.carter@example.com','michael.brooks@example.com']:
                        self.assertNotIn(original.encode(),data)
                    self.assertIn(b'K7QX2M',data)
                self.assertIsNone(item['RetainedFile'])
                self.assertIsNone(item['ErrorReport'])


if __name__ == '__main__':
    unittest.main()
