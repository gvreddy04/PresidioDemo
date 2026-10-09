"""Complete SEATS verification for the supplied export and fictional two-passenger fixture."""
from pathlib import Path
from collections import Counter
import hashlib
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET

from test_feed_listener import PROJECT, Listener, wait_for, rejected


def payload(raw):
    return bytes.fromhex("".join(line[2:] for line in raw.decode("ascii").splitlines() if line.startswith("X ")))


def header(raw):
    return b"".join(line for line in raw.splitlines(keepends=True) if line.startswith(b"A "))


def escaped(key):
    return key.replace("~", "~0").replace("/", "~1")


def verify_output(original, protected):
    rules = {field.get("name"): field.attrib for feed in ET.parse(PROJECT / "src" / "config" / "feed-policies.xml").getroot()
             if feed.get("type") == "Seats" for field in feed}
    if header(original) != header(protected):
        raise AssertionError("MQ metadata changed")
    before, after = json.loads(payload(original)), json.loads(payload(protected))
    counts = Counter()
    paths = {}

    def walk(left, right, path=""):
        if type(left) is not type(right):
            raise AssertionError("JSON type changed at " + path)
        if isinstance(left, dict):
            if list(left) != list(right):
                raise AssertionError("JSON property order/structure changed at " + path)
            for key in left:
                walk(left[key], right[key], path + "/" + escaped(key))
        elif isinstance(left, list):
            if len(left) != len(right):
                raise AssertionError("JSON array count changed at " + path)
            for a, b in zip(left, right):
                walk(a, b, path + "/*")
        else:
            if path not in rules:
                raise AssertionError("Unconfigured field at " + path)
            rule = rules[path]
            action = rule["action"]
            if action == "Keep":
                if left != right:
                    raise AssertionError("Kept field changed at " + path)
            elif action == "Replace" and rule["identification"] == "Known":
                expected = left if left in (None, "") else rule["replacement"]
                if right != expected or (left not in (None, "") and right == left):
                    raise AssertionError("Known-field replacement failed at " + path)
            else:
                raise AssertionError("Unexpected SEATS policy requiring a review at " + path)
            counts[action] += 1
            paths.setdefault(path, {"identification": rule["identification"], "action": action, "occurrences": 0})["occurrences"] += 1
    walk(before, after)
    return {"source_sha256": hashlib.sha256(original).hexdigest(),
            "output_sha256": hashlib.sha256(protected).hexdigest(),
            "scalar_values": sum(counts.values()), "unique_paths": len(paths),
            "actions": dict(counts), "mq_metadata_unchanged": True,
            "json_structure_types_and_order_unchanged": True, "field_results": paths}


class SeatsFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        (PROJECT / ".test-runs").mkdir(exist_ok=True)
        cls.sandbox = tempfile.TemporaryDirectory(prefix="seats-review-", dir=PROJECT / ".test-runs")
        cls.docs = Path(cls.sandbox.name) / "docs"
        try:
            cls.listener = Listener(cls.docs)
        except BaseException:
            cls.sandbox.cleanup()
            raise

    @classmethod
    def tearDownClass(cls):
        try:
            cls.listener.stop()
        finally:
            cls.sandbox.cleanup()

    def deliver(self, name, data):
        inbox = self.docs / "input-feeds" / "Seats" / name
        inbox.write_bytes(data)
        directory = self.docs / "output-feeds" / "Seats"
        pattern = Path(name).stem + ".*" + Path(name).suffix
        wait_for(lambda: len(list(directory.glob(pattern))) == 1 and not inbox.exists()
                 and not list((self.docs / ".processing" / "Seats").rglob(name)), "SEATS delivery and acknowledgment")
        return next(directory.glob(pattern)).read_bytes()

    def test_finalized_export_every_field(self):
        original = (PROJECT / "docs" / "sample-docs" / "finalized-feeds" / "SEATS.txt").read_bytes()
        result = verify_output(original, self.deliver("finalized-seats-review.txt", original))
        self.assertEqual(result["scalar_values"], 59)
        self.assertEqual(result["unique_paths"], 39)
        self.assertEqual(result["actions"], {"Keep": 30, "Replace": 29})
        self.assertTrue(result["mq_metadata_unchanged"])

    def test_two_passenger_fixture_every_field(self):
        original = (PROJECT / "docs" / "sample-docs" / "Seats" / "seats-mq-export.txt").read_bytes()
        output = self.deliver("two-passenger-seats-review.txt", original)
        result = verify_output(original, output)
        customers = json.loads(payload(output))["OrderChangeNotif"]["Old"]["Customers"]
        self.assertEqual(len(customers), 2)
        self.assertEqual([c["UARecloc"] for c in customers], ["K7QX2M", "K7QX2M"])
        self.assertEqual([c["ServiceItems"][0]["SeatNumber"] for c in customers], ["25C", "25D"])
        self.assertGreater(result["actions"]["Replace"], 29)
        self.assertFalse(any(name in payload(output).decode() for name in ("Emily", "Carter", "Michael", "Brooks", "PAX-1001", "PAX-1002")))

    def test_unknown_sensitive_path_retains_entire_feed(self):
        original = (PROJECT / "docs" / "sample-docs" / "Seats" / "seats-mq-export.txt").read_bytes()
        document = json.loads(payload(original))
        document["OrderChangeNotif"]["Old"]["Customers"][0]["UnreviewedEmail"] = "emily.carter@example.com"
        raw = json.dumps(document).encode()
        name = "unknown-seats-review.bin"
        (self.docs / "input-feeds" / "Seats" / name).write_bytes(raw)
        rejected(self.docs, "Seats", name, raw, "without a configured protection policy")


if __name__ == "__main__":
    unittest.main()
