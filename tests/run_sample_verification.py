"""Run all supplied samples through the real feed workers and retain value-free verification evidence.

Every published sample must keep its MQ metadata, framing and PNRs, and protect every value of the frozen parity
baseline (tests/fixtures/parity-baseline.json) unless it is an approved exception (parity-exceptions.json). The two
negative samples must be rejected with their expected reasons. Exit 0 when every check passes, 1 otherwise.
"""
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "tests/unit-tests"))

from decoded_text_support import EXPECTED_REJECTIONS, baseline, verify_published
from pipeline_harness import HARNESS


def digest(data):
    """Return a SHA-256 evidence value without exposing decoded personal fields."""
    return hashlib.sha256(data).hexdigest()


started = datetime.now(timezone.utc)
run = PROJECT / ".test-runs" / f"e2e-{started:%Y%m%d-%H%M%S}-{uuid.uuid4().hex[:8]}"
output_root = run / "harness"
command = ["dotnet", str(HARNESS), "--input", str(PROJECT / "docs/sample-docs"), "--output", str(output_root)]
run.mkdir(parents=True)
with open(run / "application.log", "w", encoding="utf-8") as log:
    completed = subprocess.run(command, cwd=PROJECT, stdout=log, stderr=subprocess.STDOUT, timeout=1200)
batch = json.loads((output_root / "report.json").read_text(encoding="utf-8"))
document, exceptions = baseline()
results, totals = [], Counter()
for item in batch["Items"]:
    source = Path(item["Source"])
    name, feed = source.name, item["Feed"]
    original = source.read_bytes()
    result = {"sample": name, "feed": feed, "status": item["Status"], "source_sha256": digest(original)}
    try:
        if name in EXPECTED_REJECTIONS:
            if item["Status"] != "Rejected" or EXPECTED_REJECTIONS[name] not in item.get("Reason", ""):
                raise AssertionError(f"{name}: expected a rejection with its reason")
            if (output_root / "dead-letter" / feed / name).read_bytes() != original:
                raise AssertionError(f"{name}: dead-letter bytes differ from the input")
        else:
            if item["Status"] != "Published":
                raise AssertionError(f"{name}: {item['Status']}: {item.get('Reason', '')}")
            output = (output_root / "published" / feed / name).read_bytes()
            result["output_sha256"] = digest(output)
            counts = verify_published(feed, name, original, output, document, exceptions)
            result.update(counts)
            totals.update(counts)
        result["verification"] = "Passed"
    except (AssertionError, OSError, KeyError, ValueError) as error:
        result["verification"] = "Failed"
        result["error"] = str(error)
    results.append(result)

statuses = Counter(item["Status"] for item in batch["Items"])
passed = (completed.returncode == 2 and all(r["verification"] == "Passed" for r in results)
          and totals["protected"] + totals["approved_exceptions"] == len(document["protected"])
          and totals["pnrs_unchanged"] == len(document["pnrs"]))
summary = dict(started_at=started.isoformat(), command=command, application_exit_code=completed.returncode,
               total_samples=len(results), statuses=dict(statuses), baseline_protected=len(document["protected"]),
               protected=totals["protected"], approved_exceptions=totals["approved_exceptions"],
               baseline_pnrs=len(document["pnrs"]), pnrs_unchanged=totals["pnrs_unchanged"],
               all_checks_passed=passed, samples=results)
evidence = run / "verification.json"
evidence.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
print(json.dumps({key: value for key, value in summary.items() if key != "samples"}, indent=2))
for result in results:
    print(result["sample"], result["status"], result["verification"], result.get("error", ""))
print("Verification evidence:", evidence)
sys.exit(0 if passed else 1)
