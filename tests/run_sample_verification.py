"""Run all supplied samples through the real CLI and retain value-free verification evidence."""

from collections import Counter
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / "tests/unit-tests"))

from reference_feed_support import field_spans, inspect_fields, verify_output as verify_binary
from configuration_support import catalog, json_rules
from test_seats_feed import header, payload, verify_output as verify_json

POLICIES = {feed: json_rules(feed) for feed in ("Seats", "PNR-Linking")}
CATALOG = catalog()
for feed in ("ACI", "ETKT", "PNR"):
    CATALOG.update({path: rule for path, rule in json_rules(feed).items() if "id" not in rule})
CATALOG["KeepStructural"] = {"action": "Keep", "identification": "None"}


def require(condition, message):
    """Fail a verification explicitly, including when Python assertions are disabled."""
    if not condition:
        raise ValueError(message)


def digest(data):
    """Return a SHA-256 evidence value without exposing decoded personal fields."""
    return hashlib.sha256(data).hexdigest()


def pnrs_in_json(document, prefix=""):
    """Collect identified JSON PNR occurrences by their exact instance paths."""
    fields = {}
    if isinstance(document, dict):
        for name, value in document.items():
            path = prefix + "/" + name.replace("~", "~0").replace("/", "~1")
            if name.lower() in {"pnr", "linkedpnr", "oldpnr", "newpnr", "uarecloc", "uarecordlocator", "pnrowner"}:
                fields[path] = value
            fields.update(pnrs_in_json(value, path))
    elif isinstance(document, list):
        for index, value in enumerate(document):
            fields.update(pnrs_in_json(value, prefix + "/" + str(index)))
    return fields


def scalar_pairs(before, after, path=""):
    """Yield JSON scalar occurrences with the wildcard paths used by feed policies."""
    if isinstance(before, dict):
        for name, value in before.items():
            escaped = name.replace("~", "~0").replace("/", "~1")
            yield from scalar_pairs(value, after[name], path + "/" + escaped)
    elif isinstance(before, list):
        for left, right in zip(before, after):
            yield from scalar_pairs(left, right, path + "/*")
    else:
        yield path, before, after


samples = PROJECT / "docs/sample-docs"
sources = {source.name: source.read_bytes() for source in samples.glob("*.txt")}
require(len(sources) == 15, "Expected exactly 15 sample source files")
started = datetime.now(timezone(timedelta(hours=5, minutes=30)))
run = PROJECT / ".test-runs" / ("e2e-" + started.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
docs = run / "docs"
run.mkdir(parents=True)
command = ["dotnet", str(PROJECT / "bin/Debug/net10.0/PresidioDemo.dll"),
           "--docs-root", str(docs), "--process-folder", str(samples)]
with (run / "application.log").open("w", encoding="utf-8") as log:
    completed = subprocess.run(command, cwd=PROJECT, stdout=log, stderr=subprocess.STDOUT, timeout=180)

reports = list((run / ".runtime/docs/reports").glob("feed-batch-*.json"))
require(len(reports) == 1, "Application did not create exactly one batch report; inspect " + str(run / "application.log"))
batch = json.loads(reports[0].read_text(encoding="utf-8"))
results = []
totals = Counter()
for item in batch["Items"]:
    name = Path(item["SourceFile"]).name
    result = {"sample": name, "feed": item["Feed"], "status": item["Status"], "checks": {}}
    try:
        original = sources[name]
        require(Path(item["SourceFile"]).read_bytes() == original, "Source bytes changed")
        require(item["SourceSha256"] == digest(original), "Source hash in batch report differs")
        require(item["Status"] == "Published", "Application outcome: " + item["Status"])
        output_path = Path(item["OutputFile"])
        output = output_path.read_bytes()
        before, after = payload(original), payload(output)
        result.update(source_sha256=digest(original), output_sha256=digest(output),
                      input_payload_bytes=len(before), output_payload_bytes=len(after), output_file=str(output_path))
        require(header(original) == header(output), "MQ metadata changed")
        require(original.endswith(b"\n") == output.endswith(b"\n"), "Final newline changed")
        result["checks"].update(source_unchanged=True, mq_metadata_unchanged=True)

        if item["Feed"] in {"ACI", "ETKT", "PNR"}:
            actions = verify_binary(item["Feed"], original, output)
            fields, framing = inspect_fields(item["Feed"], original)
            spans = field_spans(item["Feed"], original)
            mutable = set()
            pnrs = []
            for field_name, start, length in spans:
                mutable.update(range(start, start + length))
                if field_name.rsplit("/", 1)[-1].lower() == "pnr":
                    require(before[start:start + length] == after[start:start + length], "Identified PNR changed")
                    pnrs.append(field_name)
            require(len(before) == len(after), "Binary payload size changed")
            require(all(before[index] == after[index] for index in range(len(before)) if index not in mutable),
                    "Fixed binary byte changed")
            for offset, length in framing:
                require(before[offset:offset + length] == after[offset:offset + length], "String count or padding changed")
            require(len(original) == len(output), "Binary MQ container byte count changed")
            before_rows = [len(line[2:]) for line in original.splitlines() if line.startswith(b"X ")]
            after_rows = [len(line[2:]) for line in output.splitlines() if line.startswith(b"X ")]
            require(before_rows == after_rows, "Binary MQ row framing changed")
            result.update(schema=item["Feed"], identified_pnr_occurrences=len(pnrs), pnr_paths=pnrs)
            result["checks"].update(pnrs_unchanged=True, binary_payload_length_unchanged=True,
                                     fixed_bytes_unchanged=True, string_counts_and_padding_unchanged=True,
                                     binary_mq_container_framing_unchanged=True, configured_field_actions_verified=True)
            result["field_results"] = [{"path": n, "value_offset": o, "byte_length": length,
                                        "action": CATALOG[policy]["action"], "kind": kind}
                                       for n, o, length, policy, kind in fields]
            if name == "ETKT_05.txt":
                require(after[488:494] == b"******" and after[500:506] == b"******", "Extra ETKT codes were not fully masked")
                result["checks"]["additional_locator_like_codes_masked"] = True
                require(before[132:140] == after[132:140], "Text0132 structural words changed")
                result["checks"]["text0132_structural_words_unchanged"] = True
        else:
            verified = verify_json(original, output, item["Feed"])
            actions = verified["actions"]
            original_json, protected_json = json.loads(before), json.loads(after)
            original_pnrs, protected_pnrs = pnrs_in_json(original_json), pnrs_in_json(protected_json)
            require(original_pnrs and original_pnrs == protected_pnrs, "Identified JSON PNR fields changed or are missing")
            result.update(identified_pnr_occurrences=len(original_pnrs), pnr_paths=list(original_pnrs),
                          field_results=verified["field_results"])
            result["checks"].update(pnrs_unchanged=True, json_structure_types_and_order_unchanged=True,
                                     configured_field_actions_verified=True)
            replacements = [(left, right) for path, left, right in scalar_pairs(original_json, protected_json)
                            if POLICIES[item["Feed"]][path]["action"] == "Replace"]
            result["replacement_values_changed"] = sum(left != right for left, right in replacements)
            result["empty_or_null_replacements_preserved"] = sum(left is None or left == "" for left, _ in replacements)
        result["actions"] = {action: actions.get(action, 0) for action in ("Keep", "Mask", "Replace")}
        totals.update(result["actions"])
        transaction = output_path.stem.rsplit(".", 1)[-1]
        log_file = docs / "feed-logs" / (item["Feed"] + "." + transaction + ".txt")
        require(log_file.is_file(), "Transaction log is missing")
        result["checks"]["transaction_log_present"] = True
        result["verification"] = "Passed"
    except (ValueError, AssertionError, OSError, KeyError) as error:
        result["verification"] = "Failed"
        result["error"] = str(error)
    results.append(result)

statuses = Counter(item["Status"] for item in batch["Items"])
require({result["sample"] for result in results} == set(sources), "Batch did not cover every supplied source")
pending = [str(path) for folder in (docs / "input-feeds", docs / "feed-processing") for path in folder.rglob("*") if path.is_file()]
failed_files = [str(path) for path in (docs / "failed-feeds").rglob("*") if path.is_file()]
summary = dict(started_at=started.isoformat(), command=command, application_exit_code=completed.returncode,
               batch_report=str(reports[0]), application_log=str(run / "application.log"),
               total_samples=len(results), statuses=dict(statuses), totals=dict(totals),
               total_identified_pnr_occurrences=sum(r.get("identified_pnr_occurrences", 0) for r in results),
               all_checks_passed=completed.returncode == 0 and all(r["verification"] == "Passed" for r in results) and not pending and not failed_files,
               pending_files=pending, retained_failure_files=failed_files, samples=results)
evidence = run / "verification.json"
evidence.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
print(json.dumps({key: value for key, value in summary.items() if key != "samples"}, indent=2))
for result in results:
    print(result["sample"], result["status"], result.get("actions", {}), result["verification"], result.get("error", ""))
print("Verification evidence:", evidence)
sys.exit(0 if summary["all_checks_passed"] else 1)
