"""Resolve original feed-policy fields and their optional shared catalog entries."""
from pathlib import Path
import xml.etree.ElementTree as ET

PROJECT = Path(__file__).resolve().parents[2]
SCHEMA = PROJECT / "src/Configuration/xdr-layouts.xml"
CATALOG = PROJECT / "src/Configuration/feed-policies.xml"
REFERENCE = PROJECT / "src/Configuration/reference-layouts.xml"
SAMPLES = PROJECT / "docs/sample-docs"


def sample(name):
    """Return a supplied sample by filename; samples live in one folder per feed."""
    matches = list(SAMPLES.glob("*/" + name))
    assert len(matches) == 1, "Expected exactly one supplied sample named " + name
    return matches[0]

# Supplied samples that parse or route correctly but must be rejected at a later stage, with the expected error.
NEGATIVE_SAMPLES = {
    "PNR_LINKING_02_Negative_Testing.txt": "The feed codec returned a field without a configured protection policy.",
    "PNR_LINKING_03_Negative_Testing.txt": "Non-string JSON fields must remain unchanged.",
    "SEATS_02_Negative_Testing.txt": "JSON MQ exports require UTF-8 CCSID 1208; no implicit code-page conversion is performed.",
}

def catalog(path=None):
    entry = ET.parse(path or CATALOG).find("catalog")
    return {} if entry is None else {rule.get("id"): rule.attrib for rule in entry}

def json_rules(feed, schema_file=None, policy_file=None):
    shared = catalog(policy_file)
    entry = ET.parse(policy_file or CATALOG).find(f"feed[@type='{feed}']")
    return {field.get("name"): shared[field.get("policy")] if field.get("policy") else field.attrib for field in entry}
