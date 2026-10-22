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
            text = log.read_bytes().decode("utf-8")
            self.assertEqual(text.count("BEFORE ANONYMIZATION"), 1)
            self.assertEqual(text.count("AFTER ANONYMIZATION"), 1)
            before, after = text.split("AFTER ANONYMIZATION")
            source = Path(item["SourceFile"])
            is_mq = source.suffix == ".txt" and ("Decoded MQ payload" in before)
            self.assertIn("Complete feed data (" + str(source.stat().st_size) + " bytes):", before)
            self.assertNotIn("Parsed fields", after)
            self.assertNotIn("Plain text:", after)
            self.assertLess(before.index("Complete feed data"), before.index("Plain text:"))
            if item["Status"] == "Published":
                self.assertIn("Parsed fields", before)
                self.assertLess(before.index("Plain text:"), before.index("Parsed fields"))
                self.assertIn("Complete feed data", after)
                output = Path(item["OutputFile"]).read_bytes()
                self.assertIn("Complete feed data (" + str(len(output)) + " bytes):", after)
                if is_mq:
                    self.assertLess(before.index("Complete feed data"), before.index("Decoded MQ payload"))
                    self.assertLess(before.index("Decoded MQ payload"), before.index("Plain text:"))
                    self.assertLess(after.index("Decoded MQ payload"), after.index("Complete feed data"))
                    self.assertIn(output.decode("utf-8"), after)
                    if item["Feed"] == "ACI":
                        self.assertIn(f"Decoded MQ payload ({len(payload(source.read_bytes()))} bytes):", before)
                        self.assertIn(f"Decoded MQ payload ({len(payload(source.read_bytes()))} bytes):", after)
            else:
                self.assertIn("No validated anonymized output was produced.", after)
                self.assertIn("Reason:", after)
            if source.suffix == ".txt":
                self.assertIn(source.read_bytes().decode("utf-8"), before)
            if item["Status"] == "Published" and source.name in ("ACI.bin", "PNR.bin", "TKT.bin"):
                self.assertIn("Emily Carter", before)
                self.assertIn("Michael Brooks", before)
                self.assertNotIn("Emily Carter", after)
                self.assertNotIn("emily.carter@example.com", after)
                self.assertIn(b"************", Path(item["OutputFile"]).read_bytes())

    def test_actual_supplied_files_report_every_outcome_and_preserve_sources(self):
        with tempfile.TemporaryDirectory(prefix="actual-batch-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            sources = root / "sources"
            shutil.copytree(PROJECT / "docs" / "sample-docs", sources)
            before = {p.name: p.read_bytes() for p in sources.iterdir()}
            process, report = self.run_batch(sources, root / "docs")
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            self.assertTrue(report["AllPublished"])
            self.assertEqual(len(report["Items"]), 15)
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
                        keep, mask = {
                            "ACI.txt": (20, 3), "ACI_02.txt": (20, 3), "ACI_03.txt": (19, 1),
                            "ACI_04.txt": (19, 2), "ACI_05.txt": (18, 1), "ACI_06.txt": (19, 5),
                            "ETKT.txt": (4, 5), "ETKT_02.txt": (4, 5), "ETKT_03.txt": (20, 5),
                            "ETKT_04.txt": (4, 5), "ETKT_05.txt": (3, 7), "ETKT_06.txt": (4, 5),
                            "PNR.txt": (30, 76),
                        }[Path(item["SourceFile"]).name]
                        expected = {"Keep": keep, "Mask": mask}
                        self.assertEqual(verify_reference_output(item["Feed"], original, output), expected)
                        if Path(item["SourceFile"]).name == "ETKT_05.txt":
                            protected = payload(output)
                            self.assertEqual(protected[36:42], b"D7DF15")
                            self.assertEqual(protected[488:494], b"******")
                            self.assertEqual(protected[500:506], b"******")
                        if Path(item["SourceFile"]).name == "ACI_05.txt":
                            self.assertEqual(payload(output)[424:437], b"*" * 13)
                    else:
                        result = verify_output(original, output, item["Feed"])
                        expected = {"Keep": 30, "Replace": 29} if item["Feed"] == "Seats" else {"Keep": 54, "Replace": 22}
                        self.assertEqual(result["actions"], expected)
                else:
                    self.assertIsNone(item["OutputFile"])
                    self.assertEqual(Path(item["RetainedFile"]).read_bytes(), original)
                    self.assertTrue(Path(item["ErrorReport"]).exists())


    def test_disabled_xdr_route_retains_input_without_publication(self):
        with tempfile.TemporaryDirectory(prefix="pnr-policy-guard-", dir=PROJECT / ".test-runs") as temp:
            root = Path(temp)
            sources = root / "test-only-sources"
            sources.mkdir()
            raw = struct.pack(">I", 2)
            (sources / "PNR.bin").write_bytes(raw)
            schema = ET.parse(PROJECT / "src/config/xdr-layouts.xml")
            pending = schema.find("feed[@type='PNR']")
            for field in list(pending):
                pending.remove(field)
            pending.set("configured", "false")
            schemafile = root / "disabled-schema.xml"
            schema.write(schemafile, encoding="utf-8", xml_declaration=True)
            references = ET.parse(PROJECT / "src/config/reference-layouts.xml")
            references.getroot().remove(references.find("feed[@type='PNR']"))
            referencefile = root / "references.xml"
            references.write(referencefile, encoding="utf-8", xml_declaration=True)
            process, report = self.run_batch(sources, root / "docs", ["--xdr-layout-file", str(schemafile), "--reference-layout-file", str(referencefile)])
            self.assertEqual(process.returncode, 2, process.stdout + process.stderr)
            self.assertFalse(report["AllPublished"])
            self.assertEqual(len(report["Items"]), 1)
            item = report["Items"][0]
            self.assertEqual(item["Status"], "Rejected")
            self.assertIsNone(item["OutputFile"])
            self.assertEqual(Path(item["RetainedFile"]).read_bytes(), raw)
            self.assertIn("binary producer schema is not configured", Path(item["ErrorReport"]).read_text(encoding="utf-8"))
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
            references = ET.Element('referenceLayouts')
            policies = ET.parse(PROJECT / 'src/config/feed-policies.xml')
            for feed in ['ACI', 'PNR', 'ETKT']:
                entry = ET.SubElement(layout, 'feed', type=feed, configured='true', preserveWidths='true')
                ET.SubElement(entry, 'uint32', name='Version', expected='2')
                ET.SubElement(entry, 'string', name='pnr', max='6')
                array = ET.SubElement(entry, 'array', name='Passengers', max='10')
                item = ET.SubElement(array, 'struct', name='Item')
                for name in ['Name','PassengerId','Email']:
                    ET.SubElement(item, 'string', name=name, max='128')
                ref = ET.SubElement(references, 'feed', type=feed, name='TestOnly', schemaDriven='true')
                ET.SubElement(ref, 'text', name='pnr', offset='4', length='6')
                for name,offset,width in [('Name',20,12),('PassengerId',36,8),('Email',48,24)]:
                    ET.SubElement(ref, 'text', name=name, offset=str(offset), length=str(width), path='/Passengers/*/'+name)
                policy = policies.find("feed[@type='"+feed+"']")
                for field in list(policy):
                    policy.remove(field)
                for name,rule in [('pnr','KeepPnr'),('Name','MaskPerson'),('PassengerId','MaskUnclassifiedIdentifier'),('Email','MaskEmailAddress')]:
                    if name == 'pnr':
                        ET.SubElement(policy, 'field', name='/pnr', identification='None', action='Keep')
                    else:
                        ET.SubElement(policy, 'field', name='/'+name, policy=rule)
            schemafile, referencefile, policyfile = root/'xdr.xml', root/'references.xml', root/'policies.xml'
            for file,document in [(schemafile,layout),(referencefile,references),(policyfile,policies.getroot())]:
                ET.ElementTree(document).write(file, encoding='utf-8', xml_declaration=True)
            before = {p.name:p.read_bytes() for p in sources.iterdir()}
            process,report = self.run_batch(sources, root/'docs', ['--xdr-layout-file',str(schemafile),'--reference-layout-file',str(referencefile),'--policy-file',str(policyfile)])
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
