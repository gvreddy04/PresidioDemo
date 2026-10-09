"""Verify MQ export listening, JSON protection and retained failures."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

PROJECT = Path(__file__).resolve().parents[2]
DLL = PROJECT / "bin" / "Debug" / "net10.0" / "PresidioDemo.dll"
FEEDS = ["ETKT", "PNR", "PNR-Linking", "Seats", "ACI"]


def runtime_root(docs):
    docs = Path(docs).resolve()
    return docs.parent / ".runtime" / docs.name


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
    retained = list((runtime_root(docs) / "processing" / feed).rglob(filename))
    assert len(retained) == 1 and retained[0].read_bytes() == body, "Failed input was deleted"
    wait_for(lambda: (retained[0].parent / ".failure-retained").exists(), "durable rejection marker")
    assert not list((docs / "output-feeds" / feed).glob(stem + ".*")), "Unsupported input reached output"
    return report


def check(name):
    print("PASS " + name, flush=True)


def main():
    test_parent = PROJECT / ".test-runs"
    test_parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="feed-listener-", dir=test_parent) as sandbox:
        docs = Path(sandbox) / "docs"
        listener = None
        try:
            listener = Listener(docs)
            for feed in FEEDS:
                ignored = docs / "input-feeds" / feed / "ignored.JSON"
                ignored.write_bytes(b'{"name":"Emma Carter","pnr":"K7QX2M"}')
                assert ignored.exists()
            time.sleep(1.5)
            for feed in FEEDS:
                assert (docs / "input-feeds" / feed / "ignored.JSON").exists()
                assert not list((docs / "output-feeds" / feed).iterdir())
                assert not list((docs / "failed-feeds" / feed).iterdir())
                assert not any((runtime_root(docs) / "processing" / feed).rglob("ignored.JSON"))
            check("JSON inputs are ignored in all five feeds and remain untouched")

            binary = b'\x00\x00\x00\x02'
            for feed in FEEDS:
                path = docs / "input-feeds" / feed / "unconfigured.BIN"
                path.write_bytes(binary)
                rejected(docs, feed, path.name, binary, "malformed JSON" if feed in ("Seats", "PNR-Linking") else "requires 888 payload bytes" if feed == "ACI" else "requires 672 payload bytes" if feed == "ETKT" else "binary producer schema is not configured")
            check("raw binary routes retain unsupported input without publishing")

            partial = (PROJECT / "tests" / "unit-tests" / "fixtures" / "aci-mq-partial.txt").read_bytes()
            path = docs / "input-feeds" / "ACI" / "partial.txt"
            path.write_bytes(partial)
            report = rejected(docs, "ACI", path.name, partial, "560 of 920")
            original_report = report.read_bytes()
            check("partial IBM MQ dump is retained with its original bytes and a text error report")

            complete = b"AMQSBCG0 - starts here\nlength - 3 of 3 bytes\n00000000:  4142 43           'ABC'\n"
            path = docs / "input-feeds" / "ACI" / "complete.txt"
            path.write_bytes(complete)
            rejected(docs, "ACI", path.name, complete, "requires 888 payload bytes")
            check("complete diagnostic dump is reconstructed and rejected for a reference-layout mismatch")

            gap = b"AMQSBCG0 - starts here\nlength - 2 bytes\n00000010:  4142 'AB'\n"
            path = docs / "input-feeds" / "ACI" / "gap.txt"
            path.write_bytes(gap)
            rejected(docs, "ACI", path.name, gap, "out of order")
            check("missing dump rows fail closed")

            plain = b"Emma Carter"
            path = docs / "input-feeds" / "ACI" / "plain.txt"
            path.write_bytes(plain)
            rejected(docs, "ACI", path.name, plain, "must be an IBM MQ diagnostic dump")
            check("ordinary text is not silently treated as a binary feed")

            faildir = docs / "failed-feeds" / "Seats"
            saved = docs / "failed-feeds" / "Seats-saved"
            faildir.rename(saved)
            faildir.write_text("Temporarily unavailable quarantine", encoding="utf-8")
            path = docs / "input-feeds" / "Seats" / "retry.bin"
            path.write_bytes(binary)
            try:
                wait_for(lambda: any("retry.bin" in line and "Cannot save failed delivery" in line for line in listener.lines), "quarantine retry")
                retained = list((runtime_root(docs) / "processing" / "Seats").rglob(path.name))
                assert len(retained) == 1 and retained[0].read_bytes() == binary
            finally:
                faildir.unlink()
                saved.rename(faildir)
            rejected(docs, "Seats", path.name, binary, "malformed JSON")
            check("failed quarantine writes retry without deleting input")

            # Complete producer SEATS export: original envelope, fields and PNR survive local protection.
            fixture = PROJECT / "tests" / "unit-tests" / "fixtures" / "seats-mq-export.txt"
            path = docs / "input-feeds" / "Seats" / "seats.txt"
            path.write_bytes(fixture.read_bytes())
            outdir = docs / "output-feeds" / "Seats"
            wait_for(lambda: len(list(outdir.glob("seats.*.txt"))) == 1 and not path.exists(), "SEATS protected output")
            output = next(outdir.glob("seats.*.txt")).read_text(encoding="utf-8")
            import json
            def decode_export(text):
                return bytes.fromhex("".join(line[2:] for line in text.splitlines() if line.startswith("X ")))
            before_text = fixture.read_text(encoding="utf-8")
            assert [x for x in output.splitlines() if x.startswith("A ")] == [x for x in before_text.splitlines() if x.startswith("A ")]
            protected = json.loads(decode_export(output))
            customers = protected["OrderChangeNotif"]["Old"]["Customers"]
            assert len(customers) == 2
            for customer in customers:
                assert customer["UARecloc"] == "K7QX2M"
                assert customer["PaxId"] == "[PASSENGER_ID]"
                assert customer["FirstName"] == customer["LastName"] == customer["BookedAsName"] == "[PASSENGER_NAME]"
                assert set(customer["ServiceItems"][0]["Attributes"]) == {"[PROTECTED_ATTRIBUTE]"}
            assert [x["ServiceItems"][0]["SeatNumber"] for x in customers] == ["25C", "25D"]
            assert not any(x in decode_export(output).decode() for x in ["Emily", "Carter", "Michael", "Brooks", "PAX-1001", "PAX-1002"])
            assert not list((runtime_root(docs) / "processing" / "Seats").rglob("seats.txt"))
            check("SEATS end-to-end processing preserves MQ metadata, PNR, passenger grouping, types and seats")

            finalized = PROJECT / "docs" / "sample-docs"
            for route, filename in [("PNR", "PNR.txt"), ("PNR-Linking", "PNR_LINKING.txt")]:
                data = (finalized / filename).read_bytes()
                path = docs / "input-feeds" / route / filename
                path.write_bytes(data)
                rejected(docs, route, filename, data, "malformed JSON" if route == "PNR-Linking" else "multiple of four" if route == "PNR" else "binary producer schema is not configured")
            check("unconfigured binary exports and truncated PNR Linking are retained byte-for-byte without output")

            from test_aci_feed import verify_output as verify_aci_output
            aci_original = (finalized / "ACI.txt").read_bytes()
            aci_input = docs / "input-feeds" / "ACI" / "finalized-aci.txt"
            aci_input.write_bytes(aci_original)
            aci_outputs = docs / "output-feeds" / "ACI"
            wait_for(lambda: len(list(aci_outputs.glob("finalized-aci.*.txt"))) == 1 and not aci_input.exists()
                     and not list((runtime_root(docs) / "processing" / "ACI").rglob(aci_input.name)), "ACI output and acknowledgment")
            assert verify_aci_output(aci_original, next(aci_outputs.glob("finalized-aci.*.txt")).read_bytes()) == {"Keep": 17, "Mask": 6}
            check("the finalized ACI reference export is protected using configuration without NLP and preserves PNR and binary framing")

            from reference_feed_support import verify_output as verify_reference_output
            etkt_original = (finalized / "ETKT.txt").read_bytes()
            etkt_input = docs / "input-feeds" / "ETKT" / "finalized-etkt.txt"
            etkt_input.write_bytes(etkt_original)
            etkt_outputs = docs / "output-feeds" / "ETKT"
            wait_for(lambda: len(list(etkt_outputs.glob("finalized-etkt.*.txt"))) == 1 and not etkt_input.exists()
                     and not list((runtime_root(docs) / "processing" / "ETKT").rglob(etkt_input.name)), "ETKT output and acknowledgment")
            assert verify_reference_output("ETKT", etkt_original, next(etkt_outputs.glob("finalized-etkt.*.txt")).read_bytes()) == {"Keep": 4, "Mask": 5}
            check("the finalized ETKT reference export preserves PNR, status text, framing and MQ metadata with five configured masks")

            path = docs / "input-feeds" / "Seats" / "finalized-seats.txt"
            original = (finalized / "SEATS.txt").read_bytes()
            path.write_bytes(original)
            wait_for(lambda: len(list(outdir.glob("finalized-seats.*.txt"))) == 1 and not path.exists(), "supplied SEATS output")
            protected_actual = json.loads(decode_export(next(outdir.glob("finalized-seats.*.txt")).read_text(encoding="utf-8")))
            original_actual = json.loads(decode_export(original.decode()))
            assert protected_actual["OrderChangeNotif"]["Old"]["Customers"][0]["UARecloc"] == original_actual["OrderChangeNotif"]["Old"]["Customers"][0]["UARecloc"]
            assert protected_actual["OrderChangeNotif"]["Old"]["Customers"][0]["FirstName"] == "[PASSENGER_NAME]"
            check("the unmodified finalized SEATS export processes successfully")

            listener.stop()
            listener = Listener(docs)
            time.sleep(1)
            assert report.read_bytes() == original_report
            assert not any("partial.txt | message" in line for line in listener.lines)
            assert list((runtime_root(docs) / "processing" / "ACI").rglob("partial.txt"))
            assert (docs / "input-feeds" / "ACI" / "ignored.JSON").exists()
            assert not any(value in "".join(listener.lines) for value in ["Emma Carter", "CARTER/EMMA"])
            assert not (docs / ".demo-token-vault").exists()
            check("restart preserves rejected input, ignored JSON, error reports and no token vault")
            listener.stop()
            listener = None
        finally:
            if listener is not None:
                print("".join(listener.lines), flush=True)
                listener.stop()
    # TemporaryDirectory removes only this run; preserve other test reports.
    check("all feed listener checks completed")


if __name__ == "__main__":
    main()