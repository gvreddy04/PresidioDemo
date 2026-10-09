# SEATS feed completion

Status: verified for the supplied complete SEATS export and the fictional two-passenger fixture. This completes the first feed under the selected supported-processing scope.

The verified flow is input feed → folder listener → MQ/JSON parser → configuration-based field identification → embedded local Presidio anonymization → original-format serialization → validation → output feed → input acknowledgment.

| Check | Result |
| --- | --- |
| Supplied payload | Complete UTF-8 JSON, 1,362 bytes |
| Configured scalar paths | 39; none missing |
| Scalar values verified individually | 59 |
| Sensitive values replaced | 29 |
| Operational values kept unchanged | 30 |
| PNR, seat and flight values | Unchanged |
| MQ metadata | Exact bytes preserved |
| JSON keys, order, arrays and scalar types | Preserved |
| Fictional two-passenger grouping | Preserved |
| Unknown sensitive field | Entire message retained; no output |
| Input acknowledgment | After successful output delivery |
| Build | Passed; no warnings or errors |
| Focused SEATS tests | 3 passed |

Protection uses irreversible replacement labels. Passenger IDs are not stable tokens in this demo. Sensitive passenger attribute entries are replaced; downstream consumers must support the configured labels. The provided fixture has customers in the Old group and an empty New group; different message shapes require reviewed path policies.

## Field policy verification

| Exact configured path | Identification | Action | Values checked |
| --- | --- | --- | ---: |
| `/OrderChangeNotif/EventVersion` | None | Keep | 1 |
| `/OrderChangeNotif/EventTime` | None | Keep | 1 |
| `/OrderChangeNotif/EventName` | None | Keep | 1 |
| `/OrderChangeNotif/EventType` | None | Keep | 1 |
| `/OrderChangeNotif/OrderId` | Known | Replace | 1 |
| `/OrderChangeNotif/OrderCreateDateTime` | None | Keep | 1 |
| `/OrderChangeNotif/OrderVersion` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/PaxId` | Known | Replace | 1 |
| `/OrderChangeNotif/Old/Customers/*/SeqInPNR` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/FirstName` | Known | Replace | 1 |
| `/OrderChangeNotif/Old/Customers/*/LastName` | Known | Replace | 1 |
| `/OrderChangeNotif/Old/Customers/*/BookedAsName` | Known | Replace | 1 |
| `/OrderChangeNotif/Old/Customers/*/UARecloc` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/UAReclocCreateDate` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/ChgReasonCode` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/ServiceItemId` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/OrderItemId` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/ServiceType` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/CreateDateTime` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/LastUpdateDateTime` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/BookingClass` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/Cabin` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/SeatNumber` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/Attributes/*` | Known | Replace | 10 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/IATAAttributes/*` | Known | Replace | 7 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/TTYAttributes/*` | Known | Replace | 6 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/OccStatus` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/FlightSegRefId` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/FlightLegRefId` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/FlightLegOffPoint` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/FlightLegDepartureTime` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/FlightLegArrivalTime` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/Source` | None | Keep | 1 |
| `/OrderChangeNotif/Old/Customers/*/ServiceItems/*/ActionType` | None | Keep | 1 |
| `/OrderChangeNotif/EventDetail/ClientId` | None | Keep | 1 |
| `/OrderChangeNotif/EventDetail/EmpId` | Known | Replace | 1 |
| `/OrderChangeNotif/EventDetail/LEID` | None | Keep | 1 |
| `/OrderChangeNotif/EventDetail/AAA` | None | Keep | 1 |
| `/OrderChangeNotif/EventDetail/AgentSine` | None | Keep | 1 |

## Review outputs

- `seats-finalized-protected-v8.txt`: protected delivery of the supplied source export.
- `seats-fictional-two-passenger-protected-v8.txt`: protected delivery of the fictional example.
- `seats-field-verification-v8.json`: field-by-field verification and checksums.

## Next feed

PNR-Linking is next. Its supplied JSON stops inside the second customer record, so it remains retained without output. A complete producer export is required to finish real-feed verification.
