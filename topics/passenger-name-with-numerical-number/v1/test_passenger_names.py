"""Acceptance tests for the proposed passenger-name fix; failures expose current gaps.

Run from the project root with its existing virtual environment. No downloads required.
The application is imported unchanged. This suite does not patch recognizers or outputs.
"""

import argparse
from collections import Counter
import importlib.metadata
import importlib.util
import json
from pathlib import Path
import re
import sys
import unittest
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[2]
sys.path.insert(0, str(PROJECT / "python"))
if importlib.util.find_spec("en_core_web_lg") is None:
    raise RuntimeError("The bundled en_core_web_lg model is missing. Use the project's prepared environment.")
import presidio_demo as demo

CASES = json.loads((HERE / "passenger-name-cases-v1.json").read_text(encoding="utf-8"))


def canonical(content, fmt):
    if fmt == "json":
        return json.loads(content)
    if fmt == "xml":
        def tree(element):
            return (element.tag, dict(element.attrib), element.text or "",
                    element.tail or "", [tree(child) for child in element])
        return tree(ET.fromstring(content))
    return content


def evaluate(case):
    diagnostics = []

    def inspect(value, field):
        raw = demo._analyze_value(demo.custom_analyzer, value, field)
        effective = demo._pnr_wins(raw)
        def serialize(results):
            return [{"entity": r.entity_type, "start": r.start, "end": r.end,
                     "text": value[r.start:r.end], "score": r.score}
                    for r in sorted(results, key=lambda r: (r.start, r.end, r.entity_type))]
        diagnostics.append({"field": field or "", "value": value,
                            "raw": serialize(raw), "effective": serialize(effective)})
        return value

    demo._map_values(case["input"], case["format"], inspect)
    actual_people = Counter((v["field"], r["text"]) for v in diagnostics
                            for r in v["raw"] if r["entity"] == "PERSON")
    expected_people = Counter((p["field"], p["text"]) for p in case["expected_person"])
    protected = demo.anonymize_default(case["input"], case["format"])
    checks = {
        "complete_person_spans": actual_people == expected_people,
        "default_output": canonical(protected, case["format"]) ==
                          canonical(case["expected_default"], case["format"]),
    }
    actual = {"values": diagnostics, "default_output": protected}
    if case.get("check_operators"):
        original = json.loads(case["input"])["passenger"]["name"]
        # The installed hash operator salts each entity. Check the whole output shape,
        # not a fixed digest; span checks separately enforce full-name coverage.
        expected = {"replace": "<PERSON>", "redact": "", "mask": "*" * len(original),
                    "hash": None}
        actual["operators"] = {}
        for mode, name in expected.items():
            output = json.loads(demo.anonymize(case["input"], "json", mode))
            actual_name = output["passenger"]["name"]
            complete = bool(re.fullmatch(r"[0-9a-f]{64}", actual_name)) if mode == "hash" \
                       else actual_name == name
            checks["operator_" + mode] = complete and \
                                        output["pnr"] == json.loads(case["input"])["pnr"]
            actual["operators"][mode] = output
    if "expected_tokenized" in case:
        tokenized = demo.tokenize(case["input"], case["format"])
        data = json.loads(tokenized)
        checks["tokenized_output"] = canonical(data["text"], case["format"]) == \
                                     canonical(case["expected_tokenized"], case["format"])
        checks["full_name_mapping"] = set(data["mapping"].get("PERSON", {})) == \
                                      set(case["expected_mapping_names"])
        restored = demo.detokenize(tokenized, case["format"])
        checks["roundtrip"] = canonical(restored, case["format"]) == \
                              canonical(case["input"], case["format"])
        actual.update(tokenized_output=data["text"], mapping=data["mapping"], restored=restored)
    return {"id": case["id"], "category": case["category"], "checks": checks,
            "passed": all(checks.values()), "actual": actual}


class PassengerNameAcceptance(unittest.TestCase):
    """Each case is a real acceptance contract, not an expected-failure placeholder."""


def make_test(case):
    def test(self):
        result = evaluate(case)
        for check, passed in result["checks"].items():
            with self.subTest(check=check):
                self.assertTrue(passed, json.dumps(result["actual"], ensure_ascii=False))
    return test


for case in CASES:
    setattr(PassengerNameAcceptance, "test_" + case["id"], make_test(case))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, help="Write current evidence to a NEW JSON path")
    args = parser.parse_args()
    if args.report:
        if args.report.exists():
            parser.error("Report already exists. Use a new path; delivered evidence is immutable.")
        results = [evaluate(case) for case in CASES]
        versions = {p: importlib.metadata.version(p) for p in
                    ("presidio-analyzer", "presidio-anonymizer", "spacy", "en-core-web-lg")}
        summary = {"cases": len(results), "passed_cases": sum(r["passed"] for r in results),
                   "failed_cases": sum(not r["passed"] for r in results),
                   "checks": sum(len(r["checks"]) for r in results),
                   "passed_checks": sum(sum(r["checks"].values()) for r in results)}
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps({"versions": versions, "summary": summary,
                                         "results": results}, indent=2, ensure_ascii=False),
                               encoding="utf-8")
        print(json.dumps(summary))
        sys.exit(0 if not summary["failed_cases"] else 1)
    unittest.main(argv=[sys.argv[0]], verbosity=2)
