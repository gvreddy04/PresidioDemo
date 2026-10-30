"""All-route harness checks. Synthetic success fixtures are explicitly not producer schemas."""
from pathlib import Path
import hashlib
import json
import struct
import tempfile
import unittest
import xml.etree.ElementTree as ET

from pipeline_harness import PROJECT, RUNS, run
from test_seats_feed import payload, verify_output
from reference_feed_support import verify_output as verify_reference_output
from configuration_support import NEGATIVE_SAMPLES, SAMPLES


class AllFeedBatchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        RUNS.mkdir(exist_ok=True)

    def run_batch(self, inputs, extra=(), expect=None, prefix="batch-"):
        harness = run(inputs, extra, prefix=prefix)
        self.addCleanup(harness.cleanup)
        self.assertEqual(harness.returncode, expect, harness.console)
        self.assertIsNotNone(harness.report, harness.console)
        return harness, harness.report

    def verify_transaction_logs(self, harness, report):
        logs = [p for p in (harness.output_root / "logs").rglob("*") if p.is_file()]
        self.assertEqual(len(logs), len(report["Items"]))
        for item in report["Items"]:
            self.assertIn(item["Status"], ("Published", "Rejected"))
            text = Path(item["Log"]).read_bytes().decode("utf-8")
            self.assertIn("Transaction: " + item["MessageId"], text)
            self.assertEqual(text.count("BEFORE ANONYMIZATION"), 1)
            self.assertEqual(text.count("AFTER ANONYMIZATION"), 1)
            before, after = text.split("AFTER ANONYMIZATION")
            source = Path(item["Source"])
            is_mq = source.suffix == ".txt" and ("Decoded MQ payload" in before)
            self.assertIn("Complete feed data (" + str(source.stat().st_size) + " bytes):", before)
            self.assertNotIn("Parsed fields", after)
            self.assertNotIn("Plain text:", after)
            self.assertLess(before.index("Complete feed data"), before.index("Plain text:"))
            if item["Status"] == "Published":
                self.assertIn("Parsed fields", before)
                self.assertLess(before.index("Plain text:"), before.index("Parsed fields"))
                self.assertIn("Complete feed data", after)
                output = Path(item["Output"]).read_bytes()
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
                self.assertIn(b"************", Path(item["Output"]).read_bytes())

    def test_actual_supplied_files_report_every_outcome_and_preserve_sources(self):
        inputs = {p.relative_to(SAMPLES).as_posix(): p.read_bytes() for p in SAMPLES.rglob("*") if p.is_file()}
        before = {Path(name).name: content for name, content in inputs.items()}
        # Negative-testing samples are rejected by design, so the batch reports exit code 2.
        harness, report = self.run_batch(inputs, expect=2, prefix="actual-batch-")
        self.assertEqual(len(report["Items"]), 20)
        self.assertEqual(report["PublishAttempts"], 20)
        self.verify_transaction_logs(harness, report)
        outcomes = {Path(item["Source"]).name: item["Status"] for item in report["Items"]}
        self.assertEqual({name for name, status in outcomes.items() if status != "Published"}, set(NEGATIVE_SAMPLES))
        self.assertTrue(all(outcomes[name] == "Rejected" for name in NEGATIVE_SAMPLES))
        for item in report["Items"]:
            name = Path(item["Source"]).name
            if name in NEGATIVE_SAMPLES:
                self.assertIn(NEGATIVE_SAMPLES[name], harness.reason(item["Feed"], name))
                self.assertEqual(item["Reason"], harness.reason(item["Feed"], name))
        for item in report["Items"]:
            name = Path(item["Source"]).name
            original = before[name]
            self.assertEqual(Path(item["Source"]).read_bytes(), original)
            self.assertEqual(item["Sha256"], hashlib.sha256(original).hexdigest())
            self.assertEqual(item["Bytes"], len(original))
            if item["Status"] == "Published":
                output = Path(item["Output"]).read_bytes()
                if item["Feed"] in ("ACI", "ETKT", "PNR"):
                    keep, mask = {
                        "ACI.txt": (20, 3), "ACI_02.txt": (20, 3), "ACI_03.txt": (19, 1),
                        "ACI_04.txt": (19, 2), "ACI_05.txt": (18, 1), "ACI_06.txt": (19, 5),
                        "ETKT.txt": (4, 5), "ETKT_02.txt": (4, 5), "ETKT_03.txt": (20, 5),
                        "ETKT_04.txt": (4, 5), "ETKT_05.txt": (5, 5), "ETKT_06.txt": (4, 5),
                        "PNR.txt": (74, 80), "PNR_02.txt": (33, 24),
                    }[name]
                    expected = {"Keep": keep, "Mask": mask}
                    self.assertEqual(verify_reference_output(item["Feed"], original, output), expected)
                    if name == "ETKT_05.txt":
                        protected = payload(output)
                        self.assertEqual(protected[36:42], b"D7DF15")
                        self.assertEqual(protected[488:494], b"FWGXKN")
                        self.assertEqual(protected[500:506], b"FWDZRF")
                    if name == "ACI_05.txt":
                        self.assertEqual(payload(output)[424:437], b"*" * 13)
                else:
                    result = verify_output(original, output, item["Feed"])
                    expected = {"SEATS.txt": {"Keep": 30, "Replace": 29}, "SEATS_03.txt": {"Keep": 32, "Replace": 19},
                                "PNR_LINKING.txt": {"Keep": 54, "Replace": 22}}[name]
                    self.assertEqual(result["actions"], expected)
            else:
                self.assertNotIn("Output", item)
                self.assertFalse((harness.output_root / "published" / item["Feed"] / name).exists())
                self.assertEqual(Path(item["DeadLetter"]).read_bytes(), original)
                self.assertTrue(Path(item["DeadLetter"] + ".reason.txt").exists())

    def test_disabled_xdr_route_retains_input_without_publication(self):
        with tempfile.TemporaryDirectory(prefix="pnr-policy-guard-", dir=RUNS) as temp:
            root = Path(temp)
            raw = struct.pack(">I", 2)
            schema = ET.parse(PROJECT / "src/PresidioDemo.Core/Configuration/xdr-layouts.xml")
            pending = schema.find("feed[@type='PNR']")
            for field in list(pending):
                pending.remove(field)
            pending.set("configured", "false")
            schemafile = root / "disabled-schema.xml"
            schema.write(schemafile, encoding="utf-8", xml_declaration=True)
            references = ET.parse(PROJECT / "src/PresidioDemo.Core/Configuration/reference-layouts.xml")
            references.getroot().remove(references.find("feed[@type='PNR']"))
            referencefile = root / "references.xml"
            references.write(referencefile, encoding="utf-8", xml_declaration=True)
            harness, report = self.run_batch({"PNR.bin": raw}, ["--xdr-layout-file", schemafile, "--reference-layout-file", referencefile],
                                             expect=2, prefix="pnr-policy-guard-")
            self.assertEqual(len(report["Items"]), 1)
            item = report["Items"][0]
            self.assertEqual(item["Status"], "Rejected")
            self.assertEqual(item["Feed"], "PNR")
            self.assertNotIn("Output", item)
            harness.assert_rejected("PNR", "PNR.bin", raw, "binary producer schema is not configured")
            self.verify_transaction_logs(harness, report)

    def test_all_five_routes_publish_with_explicit_test_only_layouts_and_complete_json(self):
        with tempfile.TemporaryDirectory(prefix="test-only-five-routes-", dir=RUNS) as temp:
            root = Path(temp)
            sources = {}
            def u32(value): return struct.pack('>I', value)
            def string(value):
                encoded = value.encode('ascii')
                return u32(len(encoded)) + encoded + bytes((-len(encoded)) % 4)
            passengers = [("Emily Carter", "PAX-1001", "emily.carter@example.com"), ("Michael Brooks", "PAX-1002", "michael.brooks@example.com")]
            binary = u32(2) + string("K7QX2M") + u32(2) + b''.join(string(value) for passenger in passengers for value in passenger)
            for name in ["ACI.bin", "PNR.bin", "TKT.bin"]:
                sources[name] = binary
            sources["SEATS.txt"] = (PROJECT / "tests" / "unit-tests" / "fixtures" / "seats-mq-export.txt").read_bytes()
            linking = [{"PNROwner": "K7QX2M", "Customers": [{"PaxId": p[1], "FirstName": p[0].split()[0], "LastName": p[0].split()[1], "UaRecordLocator": "K7QX2M", "DOB": None} for p in passengers]}]
            sources["PNR_LINKING.bin"] = json.dumps(linking).encode()
            layout = ET.Element('xdrLayouts')
            references = ET.Element('referenceLayouts')
            policies = ET.parse(PROJECT / 'src/PresidioDemo.Core/Configuration/feed-policies.xml')
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
            harness, report = self.run_batch(sources, ['--xdr-layout-file',str(schemafile),'--reference-layout-file',str(referencefile),'--policy-file',str(policyfile)],
                                             expect=0, prefix="test-only-five-routes-")
            self.assertEqual(len(report['Items']), 5)
            self.assertEqual({item['Feed'] for item in report['Items']}, {"ACI", "PNR", "ETKT", "Seats", "PNR-Linking"})
            self.verify_transaction_logs(harness, report)
            for item in report['Items']:
                self.assertEqual(item['Status'], 'Published')
                self.assertEqual(Path(item['Source']).read_bytes(), sources[Path(item['Source']).name])
                data = Path(item['Output']).read_bytes()
                if item['Feed'] == 'Seats':
                    verify_output(sources['SEATS.txt'],data)
                else:
                    for original in ['Emily','Carter','Michael','Brooks','PAX-1001','PAX-1002','emily.carter@example.com','michael.brooks@example.com']:
                        self.assertNotIn(original.encode(),data)
                    self.assertIn(b'K7QX2M',data)
                self.assertNotIn('DeadLetter', item)
                self.assertNotIn('Reason', item)
            self.assertFalse((harness.output_root / "dead-letter").exists())


if __name__ == '__main__':
    unittest.main()
