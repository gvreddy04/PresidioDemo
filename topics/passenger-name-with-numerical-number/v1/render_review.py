"""Build the self-contained review from the captured evidence. Run before delivery only."""

from collections import Counter
from html import escape
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
baseline = json.loads((HERE / "baseline-results-v1.json").read_text(encoding="utf-8"))
cases = json.loads((HERE / "passenger-name-cases-v1.json").read_text(encoding="utf-8"))
summary = baseline["summary"]
by_id = {r["id"]: r for r in baseline["results"]}
failed_checks = Counter(k for r in baseline["results"] for k, passed in r["checks"].items()
                        if not passed)


def code(value):
    return "<code>" + escape(value) + "</code>"


def example(id, label):
    case, result = next(c for c in cases if c["id"] == id), by_id[id]
    if case["format"] == "json":
        name = json.loads(case["input"])["passenger"]["name"]
        output = json.loads(result["actual"]["default_output"])["passenger"]["name"]
    else:
        name, output = case["input"], result["actual"]["default_output"]
    return f"<tr><td>{escape(label)}</td><td>{code(name)}</td><td>{code(output)}</td></tr>"


rows = []
for case in cases:
    result = by_id[case["id"]]
    status = "Pass" if result["passed"] else "Gap"
    failed = ", ".join(k.replace("_", " ") for k,v in result["checks"].items() if not v) or "All checks passed"
    expected = case["expected_default"]
    actual = result["actual"]["default_output"]
    rows.append(f'<tr><td>{code(case["id"])}<br><small>{escape(case["category"])}</small></td>'
                f'<td><span class="{status.lower()}">{status}</span><br><small>{escape(failed)}</small></td>'
                f'<td><details><summary>Input and expected output</summary><pre>{escape(case["input"])}</pre>'
                f'<p>Expected protected output</p><pre>{escape(expected)}</pre></details>'
                f'<details><summary>Current output</summary><pre>{escape(actual)}</pre></details></td></tr>')

versions = " · ".join(f"{escape(p)} {escape(v)}" for p,v in baseline["versions"].items())
examples = "".join([example("user_numeric_4", "User example · name field"),
                    example("user_roman_IV", "User example · name field"),
                    example("text_user_roman", "User example · sentence"),
                    example("json_uppercase", "PNR recognizer collision"),
                    example("boundary_adjacent_count", "Adjacent bag count")])
html = f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Passenger names with numeric and Roman suffixes</title>
<style>
:root{{color-scheme:light;--ink:#173044;--muted:#506272;--blue:#17627e;--line:#dbe4ea;--pale:#eef5f8}}
*{{box-sizing:border-box}}body{{margin:0;background:#f5f7f9;color:var(--ink);font:16px/1.6 'Segoe UI',Arial,sans-serif}}
main{{max-width:1180px;margin:32px auto;padding:0 24px 40px}}header,section{{background:white;padding:28px 32px;border:1px solid var(--line);border-radius:12px;margin-bottom:20px}}
h1{{font-size:32px;line-height:1.2;margin:8px 0 14px}}h2{{font-size:23px;line-height:1.3;margin:0 0 16px}}h3{{font-size:18px;margin:22px 0 8px}}p{{margin:8px 0 14px}}a{{color:var(--blue)}}
.eyebrow{{font-size:12px;text-transform:uppercase;letter-spacing:1px;color:var(--blue);font-weight:700}}.muted,small{{color:var(--muted)}}small{{font-size:12px;line-height:1.4;display:inline-block}}
.stats{{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:22px 0}}.stat{{background:var(--pale);padding:14px 18px;border-radius:8px}}.stat strong{{display:block;font-size:29px;line-height:1.3}}.stat span{{font-size:13px}}
.callout{{border-left:4px solid var(--blue);background:var(--pale);padding:12px 16px;margin:18px 0}}table{{border-collapse:collapse;width:100%;font-size:14px}}th{{text-align:left;background:var(--pale)}}td,th{{border-bottom:1px solid var(--line);padding:13px 12px;vertical-align:top}}th:first-child{{width:23%}}
code{{font:13px/1.5 Consolas,monospace;overflow-wrap:anywhere;background:#f3f6f8;padding:2px 4px;border-radius:3px}}pre{{font:12px/1.5 Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere;background:#f3f6f8;padding:12px;border-radius:6px}}
li{{margin-bottom:9px}}ol,ul{{padding-left:24px}}.pass,.gap{{font-weight:700;font-size:12px;padding:3px 9px;border-radius:12px;display:inline-block}}.pass{{color:#166345;background:#e3f4eb}}.gap{{color:#8c381c;background:#fff0e4}}summary{{cursor:pointer;color:var(--blue);padding:4px 0}}details p{{font-size:12px;margin-bottom:4px}}
@media(max-width:700px){{main{{padding:0 12px}}header,section{{padding:20px 16px}}.stats{{grid-template-columns:repeat(2,1fr)}}h1{{font-size:26px}}table{{font-size:12px}}td,th{{padding:9px 6px}}}}
@media print{{body{{background:white}}main{{max-width:none;margin:0;padding:0}}header,section{{break-inside:avoid;border-radius:0}}details{{display:block}}}}
</style></head><body><main>
<header><div class="eyebrow">Technical review and implementation plan</div>
<h1>Passenger names with numeric and Roman suffixes</h1>
<p>Confirmed: passenger-name protection depends on the surrounding text and can leave a surname or suffix exposed. Protect the complete value of a trusted name field, then repair free-text boundaries using local passenger rules and names extracted from the same message.</p>
<p class="muted">Branch: {code('features/passenger-name-with-numerical-number')}<br>Project: {code('Projects/PresidioDemo')} · Application behavior remains at the reviewed baseline; this delivery adds tests and a plan.</p>
<div class="stats"><div class="stat"><strong>{summary['cases']}</strong><span>Acceptance cases</span></div><div class="stat"><strong>{summary['passed_cases']}</strong><span>Cases passing now</span></div><div class="stat"><strong>{summary['failed_cases']}</strong><span>Cases exposing gaps</span></div><div class="stat"><strong>{summary['checks']}</strong><span>Individual checks</span></div></div>
<p class="muted">Measured locally: {versions}. The .NET console, embedding Python through Python.NET, reproduced the same name spans and the uppercase PNR collision.</p>
</header>
<section><h2>What the review found</h2>
<table><thead><tr><th>Scenario</th><th>Input</th><th>Current default protection</th></tr></thead><tbody>{examples}</tbody></table>
<p>The supplied “Martin Luther King” examples are retained as user-reported reproductions. Additional passenger samples use fictional U.S.-style names and matching example.com addresses.</p>
<div class="callout"><strong>The Roman suffix is not universally missed.</strong> The full value {code('Martin Luther King IV')} is detected in a name field, but the sentence variant splits into {code('Martin Luther')} and {code('IV')}, exposing {code('King')}. Merely adding a suffix to the end of an existing PERSON span will not fix this partial surname boundary.</div>
<ul><li>{code('_analyze_value')} supplies the key/tag as context, while spaCy processes the value text. Context may enhance recognizer confidence; it does not guarantee a full-field name span.</li>
<li>Only PNR and passenger-ID recognizers are customized. There is no local passenger-name recognizer or mandatory protection for known name fields.</li>
<li>{code('_pnr_wins')} suppresses every overlapping detection. Its broad six-character PNR pattern matches {code('CARTER')} inside an uppercase name and removes the complete PERSON result from protection.</li>
<li>Default protection fails in {failed_checks['default_output']} cases; complete PERSON spans fail in {failed_checks['complete_person_spans']} cases. All four generic operators leave {code('King 4')} behind for the numeric user example.</li>
<li>The three token/recovery cases all restore the document, but all three leave an incomplete protected name or incomplete PERSON mapping. A successful round trip alone does not demonstrate PII protection.</li></ul>
<p>These are measurements for the installed model and this illustrative test corpus, not a general accuracy estimate for Presidio or production passenger feeds.</p>
</section>
<section><h2>Proposed behavior</h2>
<table><thead><tr><th>Input location</th><th>Protection rule</th><th>Expected result</th></tr></thead><tbody>
<tr><td>Trusted passenger-name field</td><td>Produce one complete PERSON span over the string value, independently of NLP success or suffix validity.</td><td>{code('Emily Carter 4')} and {code('Emily Carter IV')} both become {code('[PASSENGER_NAME]')}.</td></tr>
<tr><td>Remarks in a structured message</td><td>Find complete passenger names extracted from that message, using bounded, longest-first literal matching; retain original offsets.</td><td>Repeated names, including the suffix, are protected in full.</td></tr>
<tr><td>Standalone text with a passenger label</td><td>A local recognizer proposes the complete name candidate after Passenger/Passengers, including an allowed suffix; it can recover a missing surname token.</td><td>{code('Passenger Martin Luther King IV requests an aisle seat.')} becomes {code('Passenger [PASSENGER_NAME] requests an aisle seat.')}.</td></tr>
<tr><td>Adjacent operational values</td><td>Validate boundaries against labels, punctuation and operational terms. Repair an overlong NLP span when it includes a bag count.</td><td>{code('Emily Carter 4 bags checked.')} becomes {code('[PASSENGER_NAME] 4 bags checked.')}.</td></tr>
<tr><td>PNR/source identity</td><td>Use schema and explicit identifier context to recognize booking locators; give trusted name fields priority over incidental PNR-pattern matches.</td><td>PNR remains visible and unchanged; uppercase names are protected.</td></tr>
</tbody></table>
<p>Initial free-text suffix policy: ASCII numerals 1–10 and canonical Roman numerals I–X, case-insensitive, following a validated name candidate. This is a conservative proposed scope, not an airline naming standard. Known fields are fully protected for any supplied string, including 004, 999, IIV and Unicode Ⅳ; invalid naming data can be validated separately. Unicode comparisons must preserve an index mapping if normalization changes string length.</p>
<p>An ambiguous isolated fragment such as {code('Emily Carter 4')} can denote a suffix or operational data. Resolve it through the feed schema, an explicit passenger label or a same-message known name. If the ambiguity affects safe publication, quarantine the message for review instead of guessing and publishing a partial name.</p>
</section>
<section><h2>Implementation sequence</h2>
<ol>
<li><strong>Carry trusted field metadata.</strong> Extend {code('_map_values')} with document paths and passenger-role metadata, preserving the existing JSON/XML structure. Define feed-specific allowlists for name, fullName, passengerName, givenName and surname under passenger records. Cover XML elements, attributes and namespace-aware paths. A generic {code('product.name')} must not automatically become a passenger-name field. Treat the supplied schemas as illustrative; confirm actual ETKT, ACI and TKT adapters before production integration.</li>
<li><strong>Guarantee name-field protection.</strong> At the shared protection boundary, construct a {code('RecognizerResult(entity_type="PERSON", start=0, end=len(value), ...)')} for each nonempty trusted name string. Record provenance as schema-derived rather than model-derived. Null/empty values follow the schema policy. Unexpected nonstring values in required name fields fail validation and prevent publication. Mandatory field policy takes precedence over overlapping incidental NLP/PNR results.</li>
<li><strong>Add request-scoped known-name matching.</strong> Extract passenger names before walking the document. Pass an immutable collection through the call; do not store names in shared engine state. Match literal values with token boundaries, longest candidates first, preserving original casing, apostrophes, spacing and offsets. Deduplicate detection candidates without treating shared names as shared passenger identity.</li>
<li><strong>Add local free-text recognition.</strong> Introduce {code('python/passenger_name_recognizer.py')} with a {code('LocalRecognizer')} returning PERSON spans. Use explicit passenger labels plus bounded name-token candidates and canonical suffix validation; reuse provided {code('nlp_artifacts')} instead of loading another model. Validate neighboring words, preserve seat/flight/gate/bag values, and repair both short and overlong PERSON spans. A suffix-only pattern cannot recover the missing {code('King')} token.</li>
<li><strong>Resolve conflicts by provenance.</strong> Replace blanket overlap suppression in the protection path with deterministic rules: trusted passenger-name field → full PERSON; trusted locator field → preserved PNR; validated same-message name → full name span; then validated local/model candidates. Restrict free-text locator matches to explicit PNR/locator evidence. Merge duplicate/nested PERSON candidates without consuming operational text; ambiguous conflicting candidates prevent safe publication.</li>
<li><strong>Use one safe airline protection path.</strong> Apply the same spans to {code('analyze_custom')}, {code('anonymize_default')}, generic {code('anonymize')} protection modes and {code('tokenize')}. Keep option 1 as a clearly labeled built-in detection comparison. Preserve PNR under airline protection modes; update menu/README descriptions of option 2 accordingly. Demo name-value mappings include the entire original string, including the suffix.</li>
<li><strong>Validate locally before release.</strong> Require all acceptance cases to pass, then run the .NET bridge checks below and measure representative rebooking workloads. Release only after the application rejects incomplete known-name protection; retain the reviewed baseline for comparison.</li>
</ol>
<p>Continue using {code('presidio-analyzer')}, {code('presidio-anonymizer')}, spaCy {code('en_core_web_lg')} and {code('Python.Runtime')} inside the AWS-hosted .NET worker. Reuse initialized engines and acquire {code('Py.GIL()')} for calls. Package the model before deployment. Processing and custom recognition remain local in the worker, with no HTTP/REST or remote PII service calls.</p>
</section>
<section><h2>Files and responsibilities</h2>
<table><thead><tr><th>Proposed change</th><th>Responsibility</th><th>Acceptance evidence</th></tr></thead><tbody>
<tr><td>{code('python/presidio_demo.py')}</td><td>Path-aware traversal, schema spans, per-message name matching and shared conflict/protection policy.</td><td>Trusted fields are completely protected; PNR is unchanged; output remains valid JSON/XML.</td></tr>
<tr><td>{code('python/passenger_name_recognizer.py')} · new</td><td>Local suffix and boundary rules with clear provenance.</td><td>Full names in labeled text; numeric counts and operational Roman values preserved.</td></tr>
<tr><td>{code('Program.cs')} / {code('PythonHost.cs')}</td><td>Update option descriptions and pass serialized feed metadata if needed. Preserve string-based Python.NET calls.</td><td>Console and embedded execution agree with Python results; repeated calls are isolated.</td></tr>
<tr><td>Tests and fixture catalog</td><td>Promote these explicit acceptance contracts to the release checks; add production-adapter fixtures when schemas are available.</td><td>All {summary['cases']} cases and {summary['checks']} checks pass; no expected-failure or skip annotations conceal gaps.</td></tr>
<tr><td>{code('README.md')}</td><td>Document supported suffix policy, field allowlists, free-text ambiguity and operator behavior.</td><td>Examples match measured behavior after implementation.</td></tr>
</tbody></table>
<h3>Passenger identity remains separate</h3>
<p>The demo’s {code('<PERSON_0>')} dictionary tokenizes name values for illustration. It is not the production passenger-ID token vault. Production .NET resolves stable source passenger IDs using tenant + unchanged PNR + source passenger ID. Two passengers with the same name must still get different identity tokens; retries reuse the scoped identity token. Name spelling, suffix normalization and array order must never determine passenger identity.</p>
<p>Keep the token database provider-independent and connect through native clients. Select PostgreSQL, SQL Server, Oracle or an equivalent by measured load, concurrency and availability; SQLite remains a prototype option. This suffix change requires no selection or migration of the production database.</p>
</section>
<section><h2>Validation and release gates</h2>
<ul><li><strong>Already measured:</strong> the {summary['cases']}-case suite against unchanged Python functions; default outputs, exact PERSON boundaries, four protection operators, complete-name demo mappings and parsed JSON/XML recovery. {summary['passed_checks']} of {summary['checks']} checks pass now. The .NET console reproduced both reported name examples and the uppercase collision.</li>
<li><strong>Required after implementation:</strong> all checked-in acceptance contracts pass. Add recognizer-level boundary/offset tests for suffix range edges, malformed Roman forms, suffix followed by operational data, multiple names, punctuation and repeated occurrences. Preserve current negative examples and existing email/phone/card behavior.</li>
<li><strong>.NET integration:</strong> exercise JSON and XML through {code('PythonHost.Call')} for custom analysis, default protection, all four operators and demo token/recovery. Verify PNR equality, no leaked known-name characters, exact complete-name coverage, unchanged structure, and isolation across consecutive messages.</li>
<li><strong>Production-adapter gates:</strong> two passengers with one PNR; identical names with different stable source IDs; retry; reordered arrays; changed suffix on the same source ID; rebooking with trusted old/new PNR lineage. Assert identity tokens follow the agreed scope, independently of name detection. These tests are planned because the demo has no production vault or feed adapters.</li>
<li><strong>Additional traversal checks:</strong> XML mixed-content tails, namespaced attributes, unsupported feeds and malformed name types need explicit coverage before claiming complete feed protection. The current XML walker ignores tails; this review’s XML fixtures exercise elements, attributes and namespaces only.</li>
<li><strong>Safety and load:</strong> reject/quarantine the complete message when a known-name coverage or required schema validation fails. Never publish raw data as fallback. Measure startup, steady-state throughput, latency, memory and concurrent calls on representative AWS worker inputs; do not infer capacity from these examples.</li></ul>
</section>
<section><h2>Run the tests and inspect the evidence</h2>
<p>Open a terminal in the folder containing this report and use the project's existing environment and installed model. No extra test dependency is required.</p>
<pre>..\\..\\..\\.venv\\Scripts\\python.exe test_passenger_names.py</pre>
<p>The current suite exits with failure because it specifies the proposed behavior. Its failing checks are the measured defect backlog. To compare a future implementation, write a fresh report without overwriting this baseline:</p>
<pre>..\\..\\..\\.venv\\Scripts\\python.exe test_passenger_names.py --report ..\\..\\..\\docs\\output\\passenger-name-current.json</pre>
<p><a href="passenger-name-cases-v1.json">Fixture catalog</a> · <a href="baseline-results-v1.json">Measured baseline with offsets and outputs</a> · <a href="test_passenger_names.py">Runnable acceptance tests</a></p>
<details><summary>All acceptance cases</summary><table><thead><tr><th>Case</th><th>Baseline status</th><th>Inputs and outputs</th></tr></thead><tbody>{''.join(rows)}</tbody></table></details>
</section>
<section><h2>Primary references</h2>
<p><a href="https://github.com/data-privacy-stack/presidio/blob/main/docs/analyzer/adding_recognizers.md">Presidio local recognizers and registry registration</a> supports the proposed local extension. <a href="https://spacy.io/api/entityrecognizer">spaCy EntityRecognizer documentation</a> explains that NER predicts entity spans and that boundary accuracy matters. API and behavior details were also checked against the installed Presidio sources, including AnalyzerEngine, SpacyRecognizer and the salted hash operator.</p>
<p class="muted">All review assets and measured fixtures are in the project’s topic folder so they travel with the requested Git branch. Historical workspace artifacts are preserved.</p>
<p class="muted">HTML structure and local links were checked. Visual rendering was not verified because browser preview blocks local file URLs. The existing Windows console demo was exercised; AWS packaging and production feed integration remain implementation-stage validation.</p>
</section></main></body></html>'''
(HERE / "passenger-name-review-v1.html").write_text(html, encoding="utf-8")
print("Review generated from the measured baseline.")
