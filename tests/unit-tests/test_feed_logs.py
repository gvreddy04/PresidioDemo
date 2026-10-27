"""Verify transaction-log failures retain input and resume with the same ID."""
from pathlib import Path
import tempfile
import unittest

from test_feed_listener import PROJECT, Listener, processing_files, wait_for


class FeedLogTests(unittest.TestCase):
    def test_unavailable_log_folder_retains_input_then_retries_without_duplicate_logs(self):
        test_root = PROJECT / ".test-runs"
        test_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="feed-logs-", dir=test_root) as temp:
            docs = Path(temp) / "docs"
            listener = Listener(docs)
            logs = docs / "feed-logs"
            saved = docs / "feed-logs-saved"
            original = (PROJECT / "tests/unit-tests/fixtures/seats-mq-export.txt").read_bytes()
            try:
                logs.rename(saved)
                logs.write_text("Temporarily unavailable log folder", encoding="utf-8")
                inbox = docs / "input-feeds/Seats/comparison.txt"
                inbox.write_bytes(original)
                wait_for(lambda: any("Cannot save transaction log" in line for line in listener.lines), "log failure retains transaction")
                retained = processing_files(docs, "Seats", "comparison.txt")
                self.assertEqual(len(retained), 1)
                self.assertEqual(retained[0].read_bytes(), original)
                transaction = retained[0].stem.split(".")[-1]
                self.assertFalse(list((docs / "output-feeds/Seats").glob("comparison.*")))
                self.assertFalse(list((docs / "failed-feeds/Seats").glob("comparison.*")))
                logs.unlink()
                saved.rename(logs)
                wait_for(lambda: not retained[0].exists() and len(list((docs / "output-feeds/Seats").glob("comparison.*.txt"))) == 1, "publication after log recovery")
                files = list(logs.glob("*.txt"))
                self.assertEqual([p.name for p in files], ["Seats." + transaction + ".txt"])
                text = files[0].read_bytes().decode("utf-8")
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
                output = next((docs / "output-feeds/Seats").glob("comparison.*.txt")).read_bytes()
                self.assertIn("Complete feed data (" + str(len(output)) + " bytes):", after)
                self.assertIn(output.decode("utf-8"), after)
                self.assertFalse(list(logs.glob("*.pending")))
                listener.stop()
                listener = Listener(docs)
                listener.stop()
                self.assertEqual(list(logs.glob("*.txt")), files)
            finally:
                if logs.is_file():
                    logs.unlink()
                    saved.rename(logs)
                listener.stop()


if __name__ == "__main__":
    unittest.main()
