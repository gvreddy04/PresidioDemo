"""Verify the binary-only listener; all test inputs are local fixtures."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

PROJECT = Path(__file__).resolve().parents[1]
DLL = PROJECT / "bin" / "Debug" / "net10.0" / "PresidioDemo.dll"
FEEDS = ["ETKT", "PNR", "PNR-Linking", "Seats", "ACI"]


def wait_for(predicate, description, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.05)
    raise AssertionError("Timed out: " + description)


class Listener:
    def __init__(self, docs):
        self.lines = []
        self.process = subprocess.Popen(
            ["dotnet", str(DLL), "--docs-root", str(docs)], cwd=PROJECT,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8",
        )
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        wait_for(lambda: any("READY -" in line for line in self.lines) or self.process.poll() is not None, "startup")
        assert self.process.poll() is None, "".join(self.lines)

    def _read(self):
        self.lines.extend(iter(self.process.stdout.readline, ""))

    def stop(self):
        if self.process.poll() is None:
            self.process.stdin.write("\n")
            self.process.stdin.flush()
            self.process.wait(timeout=15)
        self.reader.join(timeout=3)
        assert self.process.returncode == 0, "".join(self.lines)
        self.process.stdin.close()
        self.process.stdout.close()


def rejected(docs, feed, filename, body, reason):
    stem, extension = Path(filename).stem, Path(filename).suffix
    failed_dir = docs / "failed-feeds" / feed
    wait_for(lambda: len(list(failed_dir.glob(stem + ".*.error.txt"))) == 1, "failure report")
    report = next(failed_dir.glob(stem + ".*.error.txt"))
    assert reason in report.read_text(encoding="utf-8")
    payloads = [p for p in failed_dir.glob(stem + ".*" + extension) if not p.name.endswith(".error.txt")]
    assert len(payloads) == 1 and payloads[0].read_bytes() == body
    retained = list((docs / ".processing" / feed).rglob(filename))
    assert len(retained) == 1 and retained[0].read_bytes() == body, "Failed input was deleted"
    wait_for(lambda: (retained[0].parent / ".failure-retained").exists(), "durable rejection marker")
    assert not list((docs / "output-feeds" / feed).glob(stem + ".*")), "Unsupported input reached output"
    return report


def check(name):
    print("PASS " + name, flush=True)


def main():
    test_parent = PROJECT / ".test-runs"
    test_parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="binary-only-", dir=test_parent) as sandbox:
        docs = Path(sandbox) / "docs"
        listener = None
        try:
            listener = Listener(docs)
            for feed in FEEDS:
                ignored = docs / "solace-feeds" / feed / "ignored.JSON"
                ignored.write_bytes(b'{"name":"Emma Carter","pnr":"K7QX2M"}')
                assert ignored.exists()
            time.sleep(1.5)
            for feed in FEEDS:
                assert (docs / "solace-feeds" / feed / "ignored.JSON").exists()
                assert not list((docs / "output-feeds" / feed).iterdir())
                assert not list((docs / "failed-feeds" / feed).iterdir())
                assert not any((docs / ".processing" / feed).rglob("ignored.JSON"))
            check("JSON inputs are ignored in all five feeds and remain untouched")

            binary = b'\x00\x00\x00\x02'
            for feed in FEEDS:
                path = docs / "solace-feeds" / feed / "unconfigured.BIN"
                path.write_bytes(binary)
                rejected(docs, feed, path.name, binary, "binary producer schema is not configured")
            check("raw binary routes retain unsupported input without publishing")

            partial = (PROJECT / "docs" / "sample-docs" / "ACI" / "aci-mq-partial.txt").read_bytes()
            path = docs / "solace-feeds" / "ACI" / "partial.txt"
            path.write_bytes(partial)
            report = rejected(docs, "ACI", path.name, partial, "560 of 920")
            original_report = report.read_bytes()
            check("partial IBM MQ dump is retained with its original bytes and a text error report")

            complete = b"AMQSBCG0 - starts here\nlength - 3 of 3 bytes\n00000000:  4142 43           'ABC'\n"
            path = docs / "solace-feeds" / "ACI" / "complete.txt"
            path.write_bytes(complete)
            rejected(docs, "ACI", path.name, complete, "binary producer schema is not configured")
            check("complete dump with a short final row is reconstructed but blocked without a schema")

            gap = b"AMQSBCG0 - starts here\nlength - 2 bytes\n00000010:  4142 'AB'\n"
            path = docs / "solace-feeds" / "ACI" / "gap.txt"
            path.write_bytes(gap)
            rejected(docs, "ACI", path.name, gap, "out of order")
            check("missing dump rows fail closed")

            plain = b"Emma Carter"
            path = docs / "solace-feeds" / "ACI" / "plain.txt"
            path.write_bytes(plain)
            rejected(docs, "ACI", path.name, plain, "must be an IBM MQ diagnostic dump")
            check("ordinary text is not silently treated as a binary feed")

            faildir = docs / "failed-feeds" / "Seats"
            saved = docs / "failed-feeds" / "Seats-saved"
            faildir.rename(saved)
            faildir.write_text("Temporarily unavailable quarantine", encoding="utf-8")
            path = docs / "solace-feeds" / "Seats" / "retry.bin"
            path.write_bytes(binary)
            try:
                wait_for(lambda: any("retry.bin" in line and "Cannot save failed delivery" in line for line in listener.lines), "quarantine retry")
                retained = list((docs / ".processing" / "Seats").rglob(path.name))
                assert len(retained) == 1 and retained[0].read_bytes() == binary
            finally:
                faildir.unlink()
                saved.rename(faildir)
            rejected(docs, "Seats", path.name, binary, "binary producer schema is not configured")
            check("failed quarantine writes retry without deleting input")

            listener.stop()
            listener = Listener(docs)
            time.sleep(1)
            assert report.read_bytes() == original_report
            assert not any("partial.txt | message" in line for line in listener.lines)
            assert list((docs / ".processing" / "ACI").rglob("partial.txt"))
            assert (docs / "solace-feeds" / "ACI" / "ignored.JSON").exists()
            assert not any(value in "".join(listener.lines) for value in ["Emma Carter", "CARTER/EMMA"])
            assert not (docs / ".demo-token-vault").exists()
            check("restart preserves rejected input, ignored JSON, error reports and no token vault")
            listener.stop()
            listener = None
        finally:
            if listener is not None:
                print("".join(listener.lines), flush=True)
                listener.stop()
    test_parent.rmdir()
    check("all binary-only listener checks completed")


if __name__ == "__main__":
    main()