"""Verify the before/after transaction log written for a published message."""
import unittest

from pipeline_harness import PROJECT, run


class FeedLogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original = (PROJECT / "tests/unit-tests/fixtures/seats-mq-export.txt").read_bytes()
        cls.harness = run({"Seats/comparison.txt": cls.original}, expect=0, prefix="feed-logs-")
        cls.addClassCleanup(cls.harness.cleanup)

    def test_one_log_per_transaction_with_complete_before_and_after_sections(self):
        original = self.original
        item = self.harness.item("Seats", "comparison.txt")
        logs = list((self.harness.output_root / "logs").rglob("*"))
        self.assertEqual([p.name for p in logs if p.is_file()], ["comparison.txt.log.txt"])
        text = self.harness.log("Seats", "comparison.txt")
        self.assertIn("Transaction: " + item["MessageId"], text)
        self.assertEqual(text.count("BEFORE ANONYMIZATION"), 1)
        before, after = text.split("AFTER ANONYMIZATION")
        self.assertIn("BEFORE ANONYMIZATION", before)
        self.assertIn(original.decode("utf-8"), before)
        self.assertIn("Decoded MQ payload", before)
        self.assertIn("Decoded MQ payload", after)
        self.assertIn("Complete feed data (" + str(len(original)) + " bytes):", before)
        self.assertNotIn("Parsed fields", after)
        self.assertNotIn("Plain text:", after)
        self.assertLess(before.index("Complete feed data"), before.index("Decoded MQ payload"))
        self.assertLess(before.index("Decoded MQ payload"), before.index("Plain text:"))
        self.assertLess(before.index("Plain text:"), before.index("Parsed fields"))
        self.assertLess(after.index("Decoded MQ payload"), after.index("Complete feed data"))
        output = self.harness.published("Seats", "comparison.txt")
        self.assertIn("Complete feed data (" + str(len(output)) + " bytes):", after)
        self.assertIn(output.decode("utf-8"), after)


if __name__ == "__main__":
    unittest.main()
