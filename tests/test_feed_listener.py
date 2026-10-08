"""End-to-end listener checks using only the Python standard library."""
from pathlib import Path
import ctypes
import json
import os
import shutil
import subprocess
import tempfile
import threading
import time
import uuid

PROJECT = Path(__file__).resolve().parents[1]
DLL = PROJECT / "bin" / "Debug" / "net10.0" / "PresidioDemo.dll"
FEEDS = ["ETKT", "PNR", "PNR-Linking", "Seats", "ACI"]
WORKFLOWS = ["EtktWorkflow", "PnrWorkflow", "PnrLinkingWorkflow", "SeatsWorkflow", "AciWorkflow"]


def wait_for(predicate, description, timeout=15):
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
            ["dotnet", str(DLL), "--docs-root", str(docs)],
            cwd=PROJECT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, encoding="utf-8",
        )
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        wait_for(lambda: any("READY -" in line for line in self.lines) or
                 self.process.poll() is not None, "listener startup")
        if self.process.poll() is not None:
            raise AssertionError("Startup failed:\n" + "".join(self.lines))

    def _read(self):
        for line in self.process.stdout:
            self.lines.append(line)

    def stop(self):
        if self.process.poll() is None:
            self.process.stdin.write("\n")
            self.process.stdin.flush()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
                raise AssertionError("Listener did not stop gracefully")
        self.reader.join(timeout=3)
        assert self.process.returncode == 0, "".join(self.lines)
        self.process.stdin.close()
        self.process.stdout.close()


def files(docs, area, feed, stem):
    return list((docs / area / feed).glob(stem + ".*.json"))


def payload_files(docs, area, feed, stem):
    return [path for path in files(docs, area, feed, stem) if not path.name.endswith(".error.json")]


def delivered(docs, feed, filename, expected, count=1):
    stem = Path(filename).stem
    wait_for(lambda: len(payload_files(docs, "output-feeds", feed, stem)) == count,
             feed + " output " + filename)
    wait_for(lambda: not (docs / "solace-feeds" / feed / filename).exists(),
             feed + " input deletion " + filename)
    wait_for(lambda: not any((docs / ".processing" / feed).rglob(filename)),
             feed + " acknowledgment " + filename)
    for output in payload_files(docs, "output-feeds", feed, stem):
        assert output.read_bytes() == expected, "Output bytes changed"


def rejected(docs, feed, filename, expected):
    stem = Path(filename).stem
    wait_for(lambda: len(payload_files(docs, "failed-feeds", feed, stem)) == 1,
             "failed copy " + filename)
    wait_for(lambda: len(list((docs / "failed-feeds" / feed).glob(stem + ".*.error.json"))) == 1,
             "failure report " + filename)
    wait_for(lambda: not (docs / "solace-feeds" / feed / filename).exists() and
             not any((docs / ".processing" / feed).rglob(filename)), "rejection acknowledgment")
    assert payload_files(docs, "failed-feeds", feed, stem)[0].read_bytes() == expected
    report = json.loads(next((docs / "failed-feeds" / feed).glob(stem + ".*.error.json")).read_bytes())
    assert report["feed"] == feed and report["sourceFile"] == filename and report["error"]
    assert not payload_files(docs, "output-feeds", feed, stem)


def check(name):
    print("PASS " + name, flush=True)


def main():
    assert DLL.exists(), "Run dotnet build before these tests."
    test_parent = PROJECT / ".test-runs"
    test_parent.mkdir(exist_ok=True)
    listener = None
    with tempfile.TemporaryDirectory(prefix="feeds-", dir=test_parent) as sandbox:
        docs = Path(sandbox) / "docs"
        for feed in FEEDS:
            inbox = docs / "solace-feeds" / feed
            inbox.mkdir(parents=True)
            source = next((PROJECT / "docs" / "sample-docs" / feed).glob("*.json"))
            json.loads(source.read_bytes())
            shutil.copyfile(source, inbox / source.name)
        try:
            listener = Listener(docs)
            for feed, workflow in zip(FEEDS, WORKFLOWS):
                source = next((PROJECT / "docs" / "sample-docs" / feed).glob("*.json"))
                delivered(docs, feed, source.name, source.read_bytes())
                wait_for(lambda: any(workflow in line for line in listener.lines), workflow + " routing")
            assert not any("emily.carter@example.com" in line for line in listener.lines)
            check("all five workflows, startup backlog, byte preservation and acknowledgment")

            body = b'{"pnr":"K7QX2M","sourcePassengerId":"PAX-1001"}\n'
            repeat = docs / "solace-feeds" / "ETKT" / "repeat.json"
            for count in [1, 2]:
                repeat.write_bytes(body)
                delivered(docs, "ETKT", repeat.name, body, count)
            assert len({p.name for p in files(docs, "output-feeds", "ETKT", "repeat")}) == 2
            check("same filename creates distinct outgoing messages")

            invalid = b'{ invalid json'
            path = docs / "solace-feeds" / "Seats" / "bad.json"
            path.write_bytes(invalid)
            rejected(docs, "Seats", path.name, invalid)
            check("invalid JSON preserved in failed feed with error report")

            ignored = docs / "solace-feeds" / "PNR" / "ignored.xml"
            ignored.write_text("<booking />", encoding="utf-8")
            nested = docs / "solace-feeds" / "PNR" / "nested"
            nested.mkdir()
            (nested / "nested.json").write_bytes(body)
            time.sleep(1.5)
            assert ignored.exists() and (nested / "nested.json").exists()
            check("non-JSON files and nested folders are ignored")

            temporary = docs / "solace-feeds" / "PNR-Linking" / "renamed.tmp"
            temporary.write_bytes(body)
            time.sleep(1.5)
            assert temporary.exists()
            target = temporary.with_suffix(".JSON")
            temporary.rename(target)
            delivered(docs, "PNR-Linking", target.name, body)
            check("temporary-file rename and uppercase JSON extension")

            bom_body = b'\xef\xbb\xbf' + body
            bom = docs / "solace-feeds" / "PNR" / "bom.json"
            bom.write_bytes(bom_body)
            delivered(docs, "PNR", bom.name, bom_body)
            check("UTF-8 BOM accepted and preserved")

            if os.name == "nt":
                locked = docs / "solace-feeds" / "ACI" / "locked.json"
                locked.write_bytes(body)
                kernel = ctypes.WinDLL("kernel32", use_last_error=True)
                kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                                               ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
                kernel.CreateFileW.restype = ctypes.c_void_p
                kernel.CloseHandle.argtypes = [ctypes.c_void_p]
                kernel.CloseHandle.restype = ctypes.c_int
                handle = kernel.CreateFileW(str(locked), 0x80000000, 0, None, 3, 0, None)
                assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
                try:
                    time.sleep(2)
                    assert locked.exists()
                    assert not files(docs, "output-feeds", "ACI", "locked")
                    assert not files(docs, "failed-feeds", "ACI", "locked")
                finally:
                    kernel.CloseHandle(handle)
                delivered(docs, "ACI", locked.name, body)
                check("locked input stays pending until the producer releases it")

            outdir = docs / "output-feeds" / "ACI"
            saved = docs / "output-feeds" / "ACI-saved"
            outdir.rename(saved)
            outdir.write_text("Simulated unavailable output directory", encoding="utf-8")
            blocked = docs / "solace-feeds" / "ACI" / "output-blocked.json"
            blocked.write_bytes(body)
            try:
                rejected(docs, "ACI", blocked.name, body)
            finally:
                outdir.unlink()
                saved.rename(outdir)
            check("output-write failure preserves the input in the failed feed")

            faildir = docs / "failed-feeds" / "PNR"
            fail_saved = docs / "failed-feeds" / "PNR-saved"
            faildir.rename(fail_saved)
            faildir.write_text("Simulated unavailable failed folder", encoding="utf-8")
            retry_input = docs / "solace-feeds" / "PNR" / "failure-retry.json"
            retry_input.write_bytes(invalid)
            try:
                wait_for(lambda: any("Cannot save failed delivery" in line for line in listener.lines),
                         "retained failure pending retry")
                retained = list((docs / ".processing" / "PNR").rglob(retry_input.name))
                assert len(retained) == 1 and retained[0].read_bytes() == invalid
                assert not payload_files(docs, "output-feeds", "PNR", "failure-retry")
            finally:
                faildir.unlink()
                fail_saved.rename(faildir)
            rejected(docs, "PNR", retry_input.name, invalid)
            check("unavailable failed folder retains the payload and retries successfully")

            second = subprocess.run(["dotnet", str(DLL), "--docs-root", str(docs)],
                                    cwd=PROJECT, input="", capture_output=True, text=True, timeout=10)
            assert second.returncode == 1, second.stdout + second.stderr
            check("a second listener cannot consume the same queue folders")

            listener.stop()
            check("graceful shutdown")
            previous_lines = listener.lines
            for name, published in [("resume", False), ("already-published", True)]:
                message_id = uuid.uuid4().hex
                stage = docs / ".processing" / "PNR" / message_id
                stage.mkdir()
                (stage / (name + ".json")).write_bytes(body)
                if published:
                    (docs / "output-feeds" / "PNR" / (name + "." + message_id + ".json")).write_bytes(body)
                else:
                    (docs / "output-feeds" / "PNR" / (name + "." + message_id + ".json.pending")).write_bytes(b'partial')

            reject_id = uuid.uuid4().hex
            reject_stage = docs / ".processing" / "PNR" / reject_id
            reject_stage.mkdir()
            (reject_stage / "already-rejected.json").write_bytes(body)
            rejected_path = docs / "failed-feeds" / "PNR" / ("already-rejected." + reject_id + ".json")
            rejected_path.write_bytes(body)
            report_path = rejected_path.with_suffix(".error.json")
            original_report = json.dumps({"feed": "PNR", "sourceFile": "already-rejected.json", "error": "Earlier output failure"}).encode()
            report_path.write_bytes(original_report)

            listener = Listener(docs)
            for name in ["resume", "already-published"]:
                delivered(docs, "PNR", name + ".json", body)
            rejected(docs, "PNR", "already-rejected.json", body)
            assert report_path.read_bytes() == original_report
            assert "".join(previous_lines).count("repeat.json | message") == 2
            check("restart recovery reuses committed outputs and preserves rejected outcomes")
            listener.stop()
            listener = None
            check("all end-to-end checks completed")
        finally:
            if listener is not None:
                print("".join(listener.lines), flush=True)
                listener.stop()
    test_parent.rmdir()


if __name__ == "__main__":
    main()
