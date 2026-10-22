# Feed sample review and common structure

All 15 files in `docs/sample-docs` were reviewed without changing source bytes. Each is an MQ text export: `A` rows carry metadata and `X` rows carry hexadecimal payload bytes. There are 13 binary payloads and two JSON payloads.

The application retains the three original XML formats: reference mappings, feed policies and XDR structures. All six ACI examples parse with the same sequence, and all six ETKT examples parse with the same sequence. Reference offsets and lengths remain as reviewed baseline documentation; dynamic parsing follows ordered XDR definitions instead of selecting per-sample templates. String scanning was used during review only. The active configurations do not read source samples at runtime.

## Common binary representation

The examples use big-endian 32-bit words and counted ASCII strings padded to four bytes. A counted string consists of its byte length, that many text bytes and zero padding. An empty string is a length word of zero, with no value or padding. Missing text is therefore represented structurally, not by a special character. Actual `*` characters in populated fields are masked/placeholder content and are not universal missing-field markers.

The ACI declarations establish linked-list pointers guarded by XDR presence words, not array counts. The separate ACI decoder reconstructs their nested structures. The existing XML represents the reviewed sample family, including four variable list presences; it remains the field-policy projection. A broader decoded shape does not automatically have processing approval or policy coverage.

The initial ACI/PNR word is two; ETKT's is five. The schema checks these individual observed discriminators. The old fixed sequence `000000020000000100000002` is removed: subsequent words and string counts are parsed separately, and offsets are calculated while reading each message.

## ACI: documented structures and a dedicated module

The C-structure definitions were checked against all six supplied ACI payloads. The new `src/feeds/ACI` folder separates the XDR reader, writer, decoder, parser, encoder, serializer, models, source definitions, indicators, readable reports and route adapter. The standard application route selects `AciFeedCodec` when the reviewed schema-driven ACI configuration is loaded.

The decoder follows `seats_assigned` (ERROR=1 / DATA=2), flight/leg/compartment lists, passenger lists, passenger-flight items and nested seat, bag, registration, loyalty, profile, sequence, security, ticket, through-check-in, miscellaneous, history, consolidated-flight, codeshare, tier and transaction-time lists. Four-byte XDR scalar slots must not be confused with C memory sizes. Strings have length words and zero padding; null linked pointers have a zero presence word.

The samples contain one passenger-list node each, including those with party count two. `nbr_prty` is not a node count. The parser checks each passenger-flight `pax_id` against its owning passenger `paxl_id`; names, PNR and node position are not used as unique passenger identity. Numeric IDs remain unchanged by the agreed scope. No tokenization was added.

The old `seatNumber` field is `leg_list.dept_gate`, now `departureGate`. The actual passenger seat is `seat_list.seat_nbr`. The old `origin` is `flight_list.trm_cty`, now `terminatingCity`; the leg city is `leg_list.cty_code`, now `legCity`. Confirmed equipment, aircraft number, gate, sector and process fields are kept. Passenger names, actual passenger seat assignments, loyalty numbers and unresolved identifiers retain masks. PNR copies stay unchanged and must agree with the owning passenger.

All six samples contain an additional block after transaction times that is not defined in the available structure declarations. With explicit approval, it is represented as `unresolved_extension`: three observed scalar words, two counted strings and a next pointer. Its business meaning is unconfirmed. The first string stays masked; the second retains its reviewed numeric-prefix mask and optional unchanged PNR suffix.

Every supplied sample passes complete byte-exact decode/encode and original MQ-export round trips. A separate Python decoder agrees on every scalar value, path, offset and width. The serializer rebuilds the complete XDR payload with the ACI encoder rather than patching source bytes or calling the generic XDR writer. It preserves scalar values, lengths, pointer flags, padding, PNRs and MQ metadata.

The existing XML sequence is retained as a conservative policy projection for this sample family. Every nonempty decoded string must be completely covered by a reviewed field rule. Unreviewed shapes are retained without publication. A synthetic two-passenger case verifies decoder identity links but is intentionally rejected by the current processing projection. No populated sample verifies some documented branches, including the unusual placement of `fcpr_list.next` before spare members. Updated producer XDR routines are still needed to establish wider production support.

The immutable workspace review, decoded examples, searchable transcription, verification evidence are under `D:\Bala_Support\topics\aci\v2`.

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
| ACI.txt | 888 | 20 | 3 Mask |
| ACI_02.txt | 888 | 20 | 3 Mask |
| ACI_03.txt | 832 | 19 | 1 Mask |
| ACI_04.txt | 876 | 19 | 2 Mask |
| ACI_05.txt | 832 | 18 | 1 Mask |
| ACI_06.txt | 960 | 19 | 5 Mask |
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

## Reviewed field names

ACI aliases now use the documented meanings described above. Earlier ETKT and PNR alias work is retained, with unresolved business meanings labeled accordingly. JSON property paths are unchanged.

| Feed | Original field | Policy alias | Evidence and limits |
| --- | --- | --- | --- |
| ACI | `recordTimestamp` | `flightAciTimestamp` | Structure `flight_list.flt_list_str3`, ACI timestamp in GMT |
| ACI | `recordTime` | `aciTransactionTime` | Structure `trans_times_list.trans_time3` |
| ACI | `secondaryAirlineCode` | `passengerFlightAirline` | Structure `paxflt_items.airline_code` |
| ETKT | `Text0172` | `ticketStatusRecord` | Eight-character masked prefix followed by readable status text; the `StatusText` capture is kept |
| PNR | `NameText0400`, `NameText0440` | `passengerSurname`, `passengerGivenName` | Match the surname/given-name pair in DOCS and contact records |
| PNR | `Docs0496`, `ContactEmail0876`, `ContactPhone0972` | `travelDocumentRecord`, `emailContactRecord`, `phoneContactRecord` | Reviewed DOCS, CTCE and CTCM prefixes and named captures |
| PNR | `NameText1880`, `NameText1984`, `NameText5276` | `passengerNameOccurrence1` through `passengerNameOccurrence3` | Repeated full-name occurrences; suffixes do not imply different passengers |
| PNR | Address, membership, account, ticket and fare records with numeric offset suffixes | Descriptive record aliases such as `addressPostalCodeRecord`, `loyaltyMembershipRecord`, `accountBalanceRecord`, `ticketOrderRecord` and `totalFare` | Existing reviewed record syntax/content supports these names; actions and capture widths are unchanged |

Confirmed top-level string names now agree across all three XML files. For example, XDR declares `<string name="flightAciTimestamp" max="4096" />`, the reference declares `<text name="flightAciTimestamp" offset="88" length="21" />`, and policies declare `<field name="/flightAciTimestamp" identification="None" action="Keep" />`. The 38 redundant alias paths are removed. The three nested ACI optional-string paths remain explicit because their reference aliases differ from their structural paths. Existing explicit path aliases remain supported by the loader.

The XML projection retains field order, word counts, bounds, optional groups and width-preservation settings. ACI `Text0020` is now `flightDate`, established by `flight_list.flt_date` in the structure declarations. The ACI module additionally validates semantic structure and passenger links. Existing custom configurations need matching name/path changes for renamed XDR keys and policies. `Text0132` remains a removed false string inference, not a renamed field. A configuration regression check requires every XDR string to have exactly one reference mapping and every exposed scalar/capture to have exactly one policy, with consistent top-level names and configured flags.

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
    <text name="passengerName" offset="424" length="11" />
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
    <string name="passengerName" max="4096" />
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
    <field name="/passengerName" policy="MaskPerson" />
  </feed>
</feedPolicies>
```

The catalog contains 14 shared masking rules. Existing inline policies work without a catalog; the Seats and PNR-Linking blocks retain their original inline format. `maskCharacters="all"` masks the actual input width. Empty and already masked values remain unchanged. Binary text accepts Keep or Known ASCII masks; PNR aliases must resolve to Keep.

Mixed record mappings stay in the reference file:

```xml
<text name="unresolvedRecordReference" offset="856" length="15"
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

The verification command retains outputs, transaction logs and a value-free `verification.json` in a fresh `.test-runs/e2e-*` folder. After the approved ACI corrections, the 15 supplied samples have 123 populated Mask fields, 268 kept fields and 51 JSON scalars with Replace policies. Forty-three replacements changed; eight empty/null values remained unchanged. All 19 identified PNR occurrences, source hashes and binary framing were preserved.

The application run published all 15 supplied feeds, with zero failures and zero pending inputs. Review evidence is retained in the workspace topic folder. The separate ACI checks cover exact XDR/MQ round trips, scalar-by-scalar independent decoder agreement, numeric passenger identity links, malformed framing, unmapped text, PNR preservation and a synthetic two-passenger decoder case. The .NET pipeline suite and 15 selected Python ACI/configuration/batch/layout regression tests passed.

ACI output differs from the earlier run where approved operational fields are now kept. Other feed rules remain unchanged. Earlier verification runs and historical artifacts are preserved. The new `aciStructures="true"` reference setting selects the ACI module; older custom configurations without the setting keep their existing codecs.
