# IR endpoint protocol 1

Transport: UTF-8 JSON objects, one compact line per frame, LF terminated, optional CR ignored.
Maximum frame length is 32768 bytes; maximum JSON nesting is eight on the endpoint. The same frames
travel over USB CDC serial at 115200 and, on firmware 0.2.0 or later with stored credentials, over
one plain TCP connection to port 7521 advertised through mDNS as `_wsgm-ir._tcp`. Every request
carries integer `v: 1`, a nonempty string `id` of at most 64 characters and `op`. Replies echo `v`
and `id`, with `status` and optional `data`. Only matching request identities may complete host
work. The host never retries an uncertain write.

| Operation             | Request fields                              | Successful reply                                                                                                             |
| --------------------- | ------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `identify` / `health` | none                                        | `ok`; identity, model, firmware, protocol, maxTimings, learning, uptimeMs, hostname, port, wifiConfigured, wifiConnected, ip |
| `learn`               | `timeoutMs`: 1000–30000                     | `learned`; data contains a self-contained payload                                                                            |
| `cancel`              | none                                        | `ok`; outstanding learn also receives `cancelled`                                                                            |
| `send`                | payload, repeats: 0–4, gapMs: 0–200         | `transmitted`, confirming emission only                                                                                      |
| `wifi`                | `ssid` ≤ 32, `password` ≤ 63, `token` 16–64 | `ok`; identity as above. Empty `ssid` clears all three                                                                       |

Firmware 0.3.0 adds three operations within protocol 1. Older firmware answers them with
`unsupported-operation`.

- `sendCode` transmits a known code through the pinned IRremoteESP8266 encoder. `protocol` is the
  library's protocol name, such as `NEC`. A protocol that carries a byte state takes `state` as
  2–128 hex digits. Every other protocol takes `value` as a hex string of at most 64 bits, optional
  `bits` (1–64, default the protocol's) and optional `repeats` (0–4, default the protocol's
  minimum). Replies are `transmitted`, `unknown-protocol`, `invalid-code` or `unsupported-protocol`.
- `sendAc` builds and transmits a complete air-conditioner state through the library's common A/C
  interface. It takes `protocol` and optional `model` (number or library name), `power`, `mode`,
  `degrees` (10–90), `celsius`, `fan`, `swingV`, `swingH`, `quiet`, `turbo`, `econo`, `light`,
  `filter`, `clean`, `beep` and `sleep`. Names follow the library's parsers. An unrecognized name is
  refused with `invalid-ac-state` rather than replaced by a default, and a protocol without A/C
  support answers `unsupported-protocol`.
- `protocols` answers `ok` with every library protocol's `name`, default `bits`, whether it carries
  a byte `state` and whether `ac` is supported.

Both send operations confirm emission only. A recognized protocol is not proof that an appliance
accepts the frame.

`learn` is asynchronous on the endpoint so cancellation and health remain available. Another learn
or send while learning receives `busy`. Learning also ends on its firmware deadline without a host,
answering `timeout`. Send is bounded to five seconds including repeats and gaps. Cancellation of a
host send wait does not imply that emission stopped. There is no automatic replay after reconnect.

Network requests must carry `token` equal to the stored pairing token; any other request except
`identify` receives `unauthorized`. `wifi` is accepted over USB only and receives `usb-only` on the
network, so pairing requires physical possession. The endpoint joins in the background after `wifi`;
poll `identify` for `wifiConnected` and `ip`. Firmware without network support answers `wifi` with
`unsupported-operation`. The endpoint serves one network client; a new connection replaces the
previous one, and a client idle for two minutes is closed. Identity fields that older firmware omits
read as unconfigured on the host.

Payload fields:

- `carrierHz`: 20000–60000. Learned default 38000 is assumed, not measured.
- `carrierSource`: `assumed`, `protocol`, `measured` or `manual`. This firmware reports `assumed`;
  measurement is not implemented by this capture path. Other endpoints must only report `measured`
  when they actually measure the carrier, not when a decoder supplies a nominal frequency.
- `timingsUs`: 2–1024 positive integers, alternating mark/space beginning with a mark. Each is
  1–65535 microseconds, with at most two seconds in one payload.
- `protocol`, `address`, `command`, `bits`, `repeat`: optional decoder metadata. Raw timings remain
  authoritative for transmission. Decoder recognition is not proof of correct appliance behavior.

Payloads with gaps beyond representable limits or capture overflow are refused, never silently
truncated. Oversized input is discarded through the next newline, then parsing recovers. Malformed,
unsupported and incompatible requests receive explicit error status. Boot diagnostics lack a valid
matching identity and cannot complete a host operation. Libraries and named scenes exist only on the
host; the endpoint has no slot API or filesystem command storage.
