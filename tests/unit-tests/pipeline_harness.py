"""Run feed inputs through the test-only broker harness and read its published, dead-letter and log files."""
from pathlib import Path
import json
import subprocess
import tempfile

PROJECT = Path(__file__).resolve().parents[2]
HARNESS = PROJECT / "tests" / "FeedPipelineHarness" / "bin" / "Debug" / "net10.0" / "FeedPipelineHarness.dll"
FEEDS = ["ETKT", "PNR", "PNR-Linking", "Seats", "ACI"]
RUNS = PROJECT / ".test-runs"


class HarnessResult:
    """One harness run: its input and output folders, exit code, console output and report."""

    def __init__(self, sandbox, input_root, output_root, process):
        self.sandbox, self.input_root, self.output_root = sandbox, input_root, output_root
        self.returncode, self.stdout, self.stderr = process.returncode, process.stdout, process.stderr
        report = output_root / "report.json"
        self.report = json.loads(report.read_text(encoding="utf-8")) if report.is_file() else None

    @property
    def console(self):
        return self.stdout + self.stderr

    def item(self, feed, name):
        """Return the single report item for a source file name routed to a feed."""
        if self.report is None:
            raise AssertionError("Harness wrote no report:\n" + self.console)
        matches = [item for item in self.report["Items"] if item["Feed"] == feed and Path(item["Source"]).name == name]
        if len(matches) != 1:
            raise AssertionError(f"Expected one report item for {feed}/{name}, found {len(matches)}")
        return matches[0]

    def status(self, feed, name, expected):
        item = self.item(feed, name)
        if item["Status"] != expected:
            raise AssertionError(f"{feed}/{name} is {item['Status']}, expected {expected}: {item.get('Reason', '')}")
        return item

    def published(self, feed, name):
        """Return the validated protected output bytes."""
        self.status(feed, name, "Published")
        return (self.output_root / "published" / feed / name).read_bytes()

    def dead_letter(self, feed, name):
        """Return the rejected original bytes."""
        self.status(feed, name, "Rejected")
        return (self.output_root / "dead-letter" / feed / name).read_bytes()

    def reason(self, feed, name):
        self.status(feed, name, "Rejected")
        return (self.output_root / "dead-letter" / feed / (name + ".reason.txt")).read_text(encoding="utf-8")

    def log(self, feed, name):
        return (self.output_root / "logs" / feed / (name + ".log.txt")).read_bytes().decode("utf-8")

    def assert_rejected(self, feed, name, body, reason):
        """Require the original bytes in the dead-letter folder, the reason, and no published output."""
        if self.dead_letter(feed, name) != body:
            raise AssertionError(f"Dead-letter bytes differ from the original {feed}/{name}")
        text = self.reason(feed, name)
        if reason not in text:
            raise AssertionError(f"Expected {reason!r} in the {feed}/{name} reason: {text}")
        if (self.output_root / "published" / feed / name).exists():
            raise AssertionError(f"Rejected input {feed}/{name} reached published output")
        return text

    def cleanup(self):
        self.sandbox.cleanup()


def run(inputs, extra_args=(), expect=None, prefix="harness-"):
    """Write inputs ({"Seats/a.txt": bytes}) into a fresh folder, run the harness once and return its result."""
    if not HARNESS.is_file():
        raise AssertionError("Build tests/FeedPipelineHarness first; missing " + str(HARNESS))
    RUNS.mkdir(exist_ok=True)
    sandbox = tempfile.TemporaryDirectory(prefix=prefix, dir=RUNS)
    root = Path(sandbox.name)
    input_root, output_root = root / "input", root / "output"
    input_root.mkdir()
    for relative, content in inputs.items():
        path = input_root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
    try:
        process = subprocess.run(["dotnet", str(HARNESS), "--input", str(input_root), "--output", str(output_root),
                                  *[str(arg) for arg in extra_args]],
                                 cwd=PROJECT, capture_output=True, text=True, encoding="utf-8", timeout=600)
        result = HarnessResult(sandbox, input_root, output_root, process)
        if expect is not None and result.returncode != expect:
            raise AssertionError(f"Harness exit {result.returncode}, expected {expect}:\n" + result.console)
        return result
    except BaseException:
        sandbox.cleanup()
        raise
