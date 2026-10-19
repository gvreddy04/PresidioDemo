# Feed sample review and common structure

All 15 files in `docs/sample-docs` were reviewed without changing source bytes. Each is an MQ text export: `A` rows carry metadata and `X` rows carry hexadecimal payload bytes. There are 13 binary payloads and two JSON payloads.

The application retains the three original XML formats: reference mappings, feed policies and XDR structures. All six ACI examples parse with the same sequence, and all six ETKT examples parse with the same sequence. Reference offsets and lengths remain as reviewed baseline documentation; dynamic parsing follows ordered XDR definitions instead of selecting per-sample templates. String scanning was used during review only. The active configurations do not read source samples at runtime.

## Common binary representation

The examples use big-endian 32-bit words and counted ASCII strings padded to four bytes. A counted string consists of its byte length, that many text bytes and zero padding. An empty string is a length word of zero, with no value or padding. Missing text is therefore represented structurally, not by a special character. Actual `*` characters in populated fields are masked/placeholder content and are not universal missing-field markers.

ACI has four groups guarded by observed 0/1 words. Zero omits the body; one includes it. The XDR layout expresses these as optional groups. Whether the producer intended an optional record or a collection with an observed maximum count of one remains unconfirmed. Values greater than one are retained without output pending reviewed configuration.

The initial ACI/PNR word is two; ETKT's is five. The schema checks these individual observed discriminators. The old fixed sequence `000000020000000100000002` is removed: subsequent words and string counts are parsed separately, and offsets are calculated while reading each message.

## ACI: one sequence, four optional groups

The 832-byte ACI_03 payload provides the reference sequence for describing size differences below. These are analysis offsets, not runtime instructions.

| Group | Presence-word position in ACI_03 | Body bytes in observed examples | Present in |
| --- | ---: | ---: | --- |
| A | 652 | 44 | ACI_06 |
| B | 664 | 44 | ACI_04, ACI_06 |
| C | 676 | 28 | ACI, ACI_02 |
| D | 724 | 28 | ACI_06 |

Positions shift when earlier groups are present. The parser follows each presence word and body instead of applying those positions.

- ACI/ACI_02: 832 + 28 from C + 12 from a populated source reference + 16 from a populated trailing reference = 888 bytes.
- ACI_04: 832 + 44 from B = 876 bytes.
- ACI_06: 832 + 44 from A + 44 from B + 28 from D + 12 from the source reference = 960 bytes.
- ACI_05: an empty reference saves four encoded bytes; the 13-character name, compared with the usual 11, adds four including padding. Total remains 832 bytes.

Airlines, routes, reviewed dates and timestamps are kept. Passenger names, uncertain codes/text and reference-like values are masked. `seatNumber` (legacy field identifier) remains uncertain and masked; the old name alone was insufficient evidence that it was a seat. ACI and ACI_02 contain a PNR copy in a trailing reference. Named captures mask its prefix and require the copy to equal the main six-character PNR. Both PNR occurrences remain unchanged.

New name widths, empty strings, reference lengths and combinations of the defined optional groups can be read without code changes. New structural members need an XML review.

## ETKT: one 136-slot sequence

Every ETKT example fits the same 110 raw words and 26 counted-string slots. Sixteen flight-information strings are empty in most examples and populated in ETKT_03. Two additional locator-like strings are empty except in ETKT_05.

- ETKT/ETKT_02/ETKT_04/ETKT_06 are 672 bytes. ETKT and ETKT_02 are byte-identical.
- ETKT_03 is 760 bytes: populated flight strings add 88 bytes to the same sequence.
- ETKT_05 is 628 bytes: its status/reference text is eight rather than 65 characters, saving 60 encoded bytes including padding. Two newly populated six-character strings add 16 encoded bytes. Thus 672 - 60 + 16 = 628.

Flight airlines/numbers, dates, departure/arrival times, routes, class and remaining status text are kept. Main PNR and event date/time stay unchanged. Ticket numbers, uncertain identifiers and the initial eight-character reference prefix are masked. ETKT_05's two additional six-character codes are masked; their PNR role is unconfirmed. Their current value offsets are 488 and 500, calculated from that input rather than stored in XML.

### Text0132 correction

The word at offset 132 is respectively 2, 2, 3, 4, 1 and 9 across the six samples. ETKT_05 happens to contain `00000001 31000000`: a scanner can incorrectly read that as a length-one string containing `1` with three zero pad bytes. ETKT_06's value nine disproves that interpretation for the common sequence. These are two independent raw words at 132 and 136. The `Text0132` text field and its mask are removed; both words are preserved unchanged in every example. ETKT_05 therefore has seven populated masked fields, rather than the previous eight.

## PNR and JSON

PNR has one available example. Its XDR definition is an ordered sequence with counted strings; the reference mappings provide named capture patterns for mixed records. DOCS, CTCE, CTCM, ticket, address, membership and payment syntax is retained while sensitive capture values are masked. It preserves the previous 76 protected spans, plus 30 kept fields containing the main PNR, operational dates/routes/flight details, fares and mileage. Forty-eight structural tag strings previously frozen inside hex templates are now represented as fixed reference parts. They remain unchanged and are excluded from the protected-field counts.

Some PNR records use `*` between adjacent fields. Shared loyalty, sensitive-attribute and financial-information rules use `#` for masking so separators remain distinguishable when the protected record is parsed again. Other masks use `*`. The output is validated and can be processed a second time without changes.

The single PNR sample does not establish a general producer contract, all collection shapes, unknown word meanings or passenger associations. Unsupported structures remain retained without publication until a reviewed schema represents them.

Seats and PNR-Linking retain their original inline feed policies with exact JSON scalar paths. Their 39 and 49 unique paths retain existing behavior: 30/54 kept scalar occurrences and 29/22 Replace occurrences. Of PNR-Linking's 22 Replace occurrences, eight are empty/null and remain unchanged. JSON property order, types, grouping, PNRs and nulls are preserved; unknown scalar paths reject the complete message.

## Reviewed samples and results

Counts below describe populated binary text/capture fields. Fixed structural tags, empty text slots and raw framing words are excluded. JSON counts describe scalar occurrences. Retained verification evidence records source hashes and calculated field spans without personal values.

| Sample | Payload bytes | Kept | Masked / Replace |
| --- | ---: | ---: | ---: |
| ACI.txt | 888 | 14 | 9 Mask |
| ACI_02.txt | 888 | 14 | 9 Mask |
| ACI_03.txt | 832 | 13 | 7 Mask |
| ACI_04.txt | 876 | 13 | 8 Mask |
| ACI_05.txt | 832 | 13 | 6 Mask |
| ACI_06.txt | 960 | 13 | 11 Mask |
| ETKT.txt | 672 | 4 | 5 Mask |
| ETKT_02.txt | 672 | 4 | 5 Mask |
| ETKT_03.txt | 760 | 20 | 5 Mask |
| ETKT_04.txt | 672 | 4 | 5 Mask |
| ETKT_05.txt | 628 | 3 | 7 Mask |
| ETKT_06.txt | 672 | 4 | 5 Mask |
| PNR.txt | 5,452 | 30 | 76 Mask |
| SEATS.txt | 1,362 | 30 | 29 Replace |
| PNR_LINKING.txt | 2,217 | 54 | 22 Replace |

All 15 published in the real application rerun. Independent sequential byte verification confirmed 19 unchanged identified PNR occurrences; exact source hashes, binary payload sizes, raw words, string counts, zero padding, record syntax, MQ headers, row framing and newline style; and the configured masks/replacements. No sample failed or remained pending. The latest retained run is linked below.

## Configuration behavior

All three files retain their original roots and feed sections. Startup validates them together before claiming input; restart applies changes. The responsibilities are:

| File | Responsibility |
| --- | --- |
| `reference-layouts.xml` | Readable field names, baseline offsets/lengths and optional capture mappings |
| `xdr-layouts.xml` | Ordered binary words, counted strings, padding, arrays and optional groups |
| `feed-policies.xml` | Per-feed Keep/Mask/Replace actions, with an optional shared catalog |

The XDR file is not technically required as a separate physical file. However, its structural information is required for variable widths and optional sections. Combining it with the reference file would require adding the same structural definitions there. Keeping the original three files preserves the existing configuration model and loader responsibilities.

## Extensions to the original approach

The listener, transport, workflows and Python host retain the original processing flow. Existing configuration filenames and CLI switches remain available. The reference codec delegates structural reading to the existing XDR codec only when `schemaDriven="true"`. Original reference configurations without this flag retain their pinned sample/template behavior and their runtime reference-file requirement. The JSON codec is unchanged; its registry adds the selection of schema-driven reference mappings before ordinary XDR routing.

Production changes extend the existing loaders/codecs for dynamic reference mappings, named captures, strict width/framing validation, optional shared policies, full-width masks and numbered batch filenames. No new production configuration-loader class or per-sample parser is introduced. Consecutive raw words use `words`, a compact alias expanded into the existing `fixedArray` primitive by the XML loader. Original `fixedArray` declarations remain supported unchanged.

## XML configuration examples

These are partial excerpts from the complete configurations; omitted fields are required in a complete layout. Developer comments in the XML explain the baseline values, uncertain fields and framing requirements.

The reference file keeps its original `name`, `offset` and `length` attributes:

```xml
<referenceLayouts>
  <feed type="ACI" name="AciReference888" schemaDriven="true"
        referenceFile="../../docs/sample-docs/ACI.txt"
        referenceSha256="f038c05e590ade818748f6547c2a624d8d662a52c54d3596ad93649eafce022d">
    <!-- Baseline positions for review; actual positions come from XDR. -->
    <text name="rawPassengerName" offset="424" length="11" />
    <text name="pnr" offset="404" length="6" pattern="[A-Z0-9]{6}" />
  </feed>
</referenceLayouts>
```

With `schemaDriven="true"`, a longer passenger name does not require changing `length="11"`. That value documents the baseline example; the wire length determines the actual width. Reference filenames and hashes are provenance only in this mode and are not read at runtime. Every XDR string must have a reference declaration, either exposing policy fields or identifying fixed parts. Unmapped strings reject configuration at startup.

The XDR file keeps the original structural primitives:

```xml
<xdrLayouts>
  <feed type="ACI" configured="true" preserveWidths="true">
    <uint32 name="Word000" expected="2" />
    <!-- Other ordered fields appear here in the complete layout. -->
    <string name="rawPassengerName" max="4096" />
  </feed>
</xdrLayouts>
```

Raw-word blocks can use the following equivalent declarations. Both consume exactly 132 bytes, with no count prefix, and produce the same scalar paths:

```xml
<!-- Original syntax remains supported. -->
<fixedArray name="Words029" count="33">
  <uint32 name="Value" />
</fixedArray>

<!-- Compact alternative; use one declaration, not both. -->
<words name="Words029" count="33" />
```

The loader normalizes `words` to the original array definition before parsing. Its positive count has the same upper bound of 1,048,576 as an existing fixed array. Child elements, unknown attributes and invalid counts reject startup. Word blocks remain in their original order, including inside optional bodies. The 107 compact blocks replace only arrays of unconstrained `uint32` values; expected constants and string definitions remain explicit.

`max` bounds a string without fixing its width. A zero length word represents an empty string. Existing optional groups use their 0/1 presence words. `preserveWidths="true"` requires unchanged total length, raw words, string counts, padding and unselected syntax after masking. No field policies or JSON mappings are embedded in this file.

The policy file retains inline feed rules and optionally reuses catalog entries:

```xml
<feedPolicies>
  <catalog>
    <policy id="MaskPerson" identification="Known" entity="PERSON"
            action="Mask" maskCharacters="all" maskCharacter="*" />
  </catalog>
  <feed type="ACI" configured="true">
    <field name="/pnr" identification="None" action="Keep" />
    <field name="/rawPassengerName" policy="MaskPerson" />
  </feed>
</feedPolicies>
```

The catalog contains 14 shared masking rules. Existing inline policies work without a catalog; the Seats and PNR-Linking blocks retain their original inline format. `maskCharacters="all"` masks the actual input width. Empty and already masked values remain unchanged. Binary text accepts Keep or Known ASCII masks; PNR aliases must resolve to Keep.

Mixed record mappings stay in the reference file:

```xml
<text name="RecordReference" offset="856" length="15"
      optional="true" allowEmpty="true"
      pattern="(?&lt;Prefix&gt;[0-9*]*)(?&lt;pnr&gt;[A-Z0-9]{6})">
  <part name="Prefix" start="0" length="9" />
  <part name="pnr" start="9" length="6" equalTo="/pnr" />
</text>
```

Part starts/lengths retain the baseline review values. Named captures determine actual spans in schema-driven mode; literal syntax outside captures stays unchanged. The copied PNR must equal the main PNR. `path` optionally maps an existing field alias to a nested XDR path; `optional` allows absence and `allowEmpty` permits an empty mixed record. Patterns match the complete string with bounded-time non-backtracking matching. Mismatches, overlapping/repeated captures and conflicting PNR copies reject input.

Invalid references, missing field rules, PNR policy overrides and incompatible binary actions fail startup. Runtime rejects truncation, trailing regions, invalid optional states, nonprintable text and nonzero padding. Unknown structure is retained without publication. Values and widths within the defined structure need no configuration changes; new structural variations require reviewed XML. A wire encoding outside the existing primitives would require an adapter.

## Reproduce verification

```powershell
dotnet build
dotnet run --project tests/FeedPipelineChecks
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/unit-tests/test_feed_listener.py
src/.venv/Scripts/python.exe tests/run_sample_verification.py
```

The verification command retains outputs, transaction logs and a value-free `verification.json` in a fresh `.test-runs/e2e-*` folder. Across the 15 supplied samples, 158 populated fields have Mask policies, 233 are kept, and 51 JSON scalars have Replace policies. Forty-three replacements changed; eight empty/null values remained unchanged. All 19 identified PNR occurrences, source hashes and binary framing were preserved.

Latest run: **20 October 2026, 01:12:09 IST**, with 15 published, zero failures and zero pending inputs. [Verification evidence](../.test-runs/e2e-20261020-011209-24a203a4/verification.json) and [outputs](../.test-runs/e2e-20261020-011209-24a203a4/docs/output-feeds) are retained locally. The run folder is ignored by Git and contains original-value transaction logs. Regression checks cover equivalent original and compact word declarations across all 13 binary samples, invalid shorthand settings, changed string widths, legacy configuration compatibility, exact no-op round trips, idempotent real Presidio protection, malformed framing, startup validation and delivery/retention behavior.
