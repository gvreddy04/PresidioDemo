"""Value-free verification of decoded-text protection against the frozen parity baseline (WR-019).

Shared by test_decoded_text_feeds.py and tests/run_sample_verification.py. Nothing here prints or returns a sample
value; failures name the sample, location and check only.
"""
from pathlib import Path
import json

PROJECT = Path(__file__).resolve().parents[2]
FIXTURES = PROJECT / "tests" / "fixtures"
BINARY = ("ACI", "ETKT", "PNR")
EXPECTED_REJECTIONS = {
    "PNR_LINKING_03_Negative_Testing.txt": "number or boolean that cannot be protected in its JSON type",
    "SEATS_02_Negative_Testing.txt": "UTF-8 CCSID 1208",
}
MASK_CHARACTERS = set(b"*#")


def payload(message):
    """Return the payload bytes of an IBM MQ export (X rows) or a raw payload."""
    lines = message.decode("utf-8", errors="strict").splitlines() if message[:2] == b"A " else None
    if lines is None:
        return message
    return bytes.fromhex("".join(line[2:].strip() for line in lines if line.startswith("X ")))


def header(message):
    """Return the MQ metadata rows of an export."""
    return [line for line in message.decode("utf-8").splitlines() if line.startswith("A ")]


def baseline():
    """Return the frozen baseline and the approved exceptions as (sample, location) keys."""
    document = json.loads((FIXTURES / "parity-baseline.json").read_text(encoding="utf-8"))
    exceptions = {(e["sample"], e["location"]) for e in json.loads((FIXTURES / "parity-exceptions.json").read_text(encoding="utf-8"))}
    return document, exceptions


def json_value(document, location):
    """Return the scalar at a JSON instance path such as /0/Customers/1/FirstName."""
    node = document
    for segment in [s for s in location.split("/") if s]:
        key = segment.replace("~1", "/").replace("~0", "~")
        node = node[int(key)] if isinstance(node, list) else node[key]
    return node


def verify_published(feed, name, original, output, document=None, exceptions=None):
    """Check one published sample; return value-free counts, raise AssertionError on a failed check."""
    if document is None:
        document, exceptions = baseline()
    before, after = payload(original), payload(output)
    checks = {"protected": 0, "approved_exceptions": 0, "pnrs_unchanged": 0}
    if original[:2] == b"A ":
        if header(original) != header(output):
            raise AssertionError(f"{name}: MQ metadata changed")
        if original.endswith(b"\n") != output.endswith(b"\n"):
            raise AssertionError(f"{name}: final newline changed")
        rows = [len(line) for line in original.splitlines() if line.startswith(b"X ")]
        # JSON replacements change the payload length, so only binary exports keep identical hex rows (WR-004).
        if feed in BINARY and rows != [len(line) for line in output.splitlines() if line.startswith(b"X ")]:
            raise AssertionError(f"{name}: MQ row framing changed")
    if feed in BINARY:
        if len(before) != len(after):
            raise AssertionError(f"{name}: binary payload length changed")
        for index, (left, right) in enumerate(zip(before, after)):
            if left != right and right not in MASK_CHARACTERS:
                raise AssertionError(f"{name}: byte {index} changed to a character that is not a mask character")
    else:
        left_json, right_json = json.loads(before), json.loads(after)
    for entry in document["protected"]:
        if entry["sample"] != name:
            continue
        if feed in BINARY:
            start, length = int(entry["location"]), entry["length"]
            caught = all(b == 0x2A or b != a for b, a in zip(before[start:start + length], after[start:start + length]))
        else:
            caught = json_value(left_json, entry["location"]) != json_value(right_json, entry["location"])
        if caught:
            checks["protected"] += 1
        elif (name, entry["location"]) in exceptions:
            checks["approved_exceptions"] += 1
        else:
            raise AssertionError(f"{name}: baseline {entry['entity']} at {entry['location']} was published unprotected")
    for entry in document["pnrs"]:
        if entry["sample"] != name:
            continue
        if feed in BINARY:
            start, length = int(entry["location"]), entry["length"]
            same = before[start:start + length] == after[start:start + length]
        else:
            same = json_value(left_json, entry["location"]) == json_value(right_json, entry["location"])
        if not same:
            raise AssertionError(f"{name}: PNR at {entry['location']} changed")
        checks["pnrs_unchanged"] += 1
    return checks
