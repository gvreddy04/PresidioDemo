"""Complete SEATS verification for the supplied export and fictional two-passenger fixture."""
from collections import Counter
import hashlib
import json
import unittest

from pipeline_harness import PROJECT, run
from configuration_support import sample


def payload(raw):
    return bytes.fromhex("".join(line[2:] for line in raw.decode("ascii").splitlines() if line.startswith("X ")))


def header(raw):
    return b"".join(line for line in raw.splitlines(keepends=True) if line.startswith(b"A "))


def escaped(key):
    return key.replace("~", "~0").replace("/", "~1")


def verify_output(original, protected, feed_type="Seats"):
    from configuration_support import json_rules
    rules = json_rules(feed_type)
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
                raise AssertionError("Unexpected configured policy requiring a review at " + path)
            counts[action] += 1
            paths.setdefault(path, {"identification": rule["identification"], "action": action, "occurrences": 0})["occurrences"] += 1
    walk(before, after)
    return {"source_sha256": hashlib.sha256(original).hexdigest(),
            "output_sha256": hashlib.sha256(protected).hexdigest(),
            "scalar_values": sum(counts.values()), "unique_paths": len(paths),
            "actions": dict(counts), "mq_metadata_unchanged": True,
            "json_structure_types_and_order_unchanged": True, "field_results": paths}


FIXTURE = PROJECT / "tests" / "unit-tests" / "fixtures" / "seats-mq-export.txt"


def unknown_path_document():
    document = json.loads(payload(FIXTURE.read_bytes()))
    document["OrderChangeNotif"]["Old"]["Customers"][0]["UnreviewedEmail"] = "emily.carter@example.com"
    return json.dumps(document).encode()


class SeatsFeedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.harness = run({"Seats/finalized-seats-review.txt": sample("SEATS.txt").read_bytes(),
                           "Seats/two-passenger-seats-review.txt": FIXTURE.read_bytes(),
                           "Seats/unknown-seats-review.bin": unknown_path_document()}, expect=2, prefix="seats-review-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_finalized_export_every_field(self):
        original = sample("SEATS.txt").read_bytes()
        result = verify_output(original, self.harness.published("Seats", "finalized-seats-review.txt"))
        self.assertEqual(result["scalar_values"], 59)
        self.assertEqual(result["unique_paths"], 39)
        self.assertEqual(result["actions"], {"Keep": 30, "Replace": 29})
        self.assertTrue(result["mq_metadata_unchanged"])

    def test_two_passenger_fixture_every_field(self):
        original = FIXTURE.read_bytes()
        output = self.harness.published("Seats", "two-passenger-seats-review.txt")
        result = verify_output(original, output)
        customers = json.loads(payload(output))["OrderChangeNotif"]["Old"]["Customers"]
        self.assertEqual(len(customers), 2)
        self.assertEqual([c["UARecloc"] for c in customers], ["K7QX2M", "K7QX2M"])
        self.assertEqual([c["ServiceItems"][0]["SeatNumber"] for c in customers], ["25C", "25D"])
        self.assertGreater(result["actions"]["Replace"], 29)
        self.assertFalse(any(name in payload(output).decode() for name in ("Emily", "Carter", "Michael", "Brooks", "PAX-1001", "PAX-1002")))

    def test_unknown_sensitive_path_retains_entire_feed(self):
        self.harness.assert_rejected("Seats", "unknown-seats-review.bin", unknown_path_document(),
                                     "without a configured protection policy")


if __name__ == "__main__":
    unittest.main()
