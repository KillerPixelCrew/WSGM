# IR endpoint protocol 2

Transport: UTF-8 JSON objects, one compact line per frame, LF terminated, optional CR ignored.
Maximum frame length is 32768 bytes; maximum JSON nesting is eight on the endpoint. The same frames
travel over USB CDC serial at 115200 and, on firmware 0.2.0 or later with stored credentials, over
one plain TCP connection to port 7521 advertised through mDNS as `_wsgm-ir._tcp`. Every request
carries integer `v: 2`, a nonempty string `id` of at most 64 characters and `op`. Replies echo `v`
and `id`, with `status` and optional `data`. Only matching request identities may complete host
work. The host never retries an uncertain write.

Firmware 0.5.0 is the first to speak protocol 2, and this WSGM needs it. Protocol 2 changes only the
`remotes` reply, which is paged so a catalog of any size fits the frame. An endpoint answers a
request in any other version with `protocol-mismatch`, and the host refuses an endpoint that
identifies with another protocol.

| Operation             | Request fields                              | Successful reply                                                                                                             |
| --------------------- | ------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `identify` / `health` | none                                        | `ok`; identity, model, firmware, protocol, maxTimings, learning, uptimeMs, hostname, port, wifiConfigured, wifiConnected, ip |
| `learn`               | `timeoutMs`: 1000–30000                     | `learned`; data contains a self-contained payload                                                                            |
| `cancel`              | none                                        | `ok`; outstanding learn also receives `cancelled`                                                                            |
| `send`                | payload, repeats: 0–4, gapMs: 0–200         | `transmitted`, confirming emission only                                                                                      |
| `wifi`                | `ssid` ≤ 32, `password` ≤ 63, `token` 16–64 | `ok`; identity as above. Empty `ssid` clears all three                                                                       |

Firmware 0.3.0 added three operations. Older firmware answers them with `unsupported-operation`.

- `sendCode` transmits a known code through the pinned IRremoteESP8266 encoder. `protocol` is the
  library's protocol name, such as `NEC`. A protocol that carries a byte state takes `state` as
  2–128 hex digits. Every other protocol takes `value` as an optional `0x` and 1–16 hex digits,
  optional `bits` (1–64, default the protocol's) and optional `repeats` (0–4, default the protocol's
  minimum). Replies are `transmitted`, `unknown-protocol`, `invalid-code` or `unsupported-protocol`.
- `sendAc` builds and transmits a complete air-conditioner state through the library's common A/C
  interface. It takes `protocol` and optional `model` (a number from -1 to 32767 or a library name),
  `power`, `mode`, `degrees` (10–90), `celsius`, `fan`, `swingV`, `swingH`, `quiet`, `turbo`,
  `econo`, `light`, `filter`, `clean`, `beep` and `sleep`. Names follow the library's parsers. An
  unrecognized name or an out-of-range model is refused with `invalid-ac-state` rather than replaced
  by a default, and a protocol without A/C support answers `unsupported-protocol`.
- `protocols` answers `ok` with every library protocol's `name`, default `bits`, whether it carries
  a byte `state` and whether `ac` is supported.

Both send operations confirm emission only. A recognized protocol is not proof that an appliance
accepts the frame.

Firmware 0.4.0 added the remotes built into it, with these further operations:

- `remotes` takes an optional integer `chunk`, 0 by default, and answers `ok` with `chunk`, a string
  holding that part of the catalog's JSON text, its `index` and the `count` of chunks. A chunk holds
  at most 8192 bytes and never ends inside a UTF-8 sequence. The host requests chunks 0 to
  `count - 1` in order, joins them and parses the catalog: each built-in remote's `id`, `name`,
  button and sequence `id` and `label` pairs and, for an air conditioner, its declared `climate`
  capabilities. A chunk that is not an integer or is out of range answers `invalid-chunk`.
- `press` takes `remote` and `button` and answers as the button's send does, or `unknown-remote` or
  `unknown-button`.
- `climate` takes `remote`, `power`, `mode`, `degrees`, `fan` and optional `toggleSwing`. A value
  outside the remote's declared modes, fans and temperature range, or a swing toggle on a remote
  without one, answers `invalid-ac-state`.
- `run` takes `remote` and `sequence` and answers `started`. The endpoint then works through the
  steps in the background, and a failed step ends the sequence. `cancel` also stops a sequence.
- `web` takes `user` (at most 32 characters, no colon) and `password` (8–64 characters). It is
  accepted over USB only and stores both on the endpoint. Empty values disable the web remotes, and
  clearing Wi-Fi clears them too.

While a learn or a sequence runs, every send, press, climate request, sequence start and learn
answers `busy`, and so does `wifi`, because the radio should not change under a running operation.
Reads such as `identify`, `remotes` and `protocols` keep answering. `identify` also reports
`webPort`, `webConfigured`, the number of built-in `remotes` and `sequenceRunning`. When the
built-in definitions fail to load, the endpoint reports no remotes and serves an empty catalog.

`learn` is asynchronous on the endpoint so cancellation and health remain available. Another learn
or send while learning receives `busy`. Learning also ends on its firmware deadline without a host,
answering `timeout`. A network learn whose connection is replaced or closed ends with `cancelled` to
that connection. Send is bounded to five seconds including repeats and gaps. Cancellation of a host
send wait does not imply that emission stopped. There is no automatic replay after reconnect.

Network requests must carry `token` equal to the stored pairing token; any other request except
`identify` receives `unauthorized`. `wifi` is accepted over USB only and receives `usb-only` on the
network, so pairing requires physical possession. The endpoint joins in the background after `wifi`;
poll `identify` for `wifiConnected` and `ip`. Firmware without network support answers `wifi` with
`unsupported-operation`. The endpoint serves one network client; a new connection replaces the
previous one, and a client idle for two minutes is closed. Identity fields that older firmware omits
read as unconfigured on the host. Each explicit host Wi-Fi action opens and identifies a fresh
connection before endpoint work; cached identity is not evidence that an idle socket remains alive.
This reconnection precedes any transmission and does not retry an uncertain operation.

`wifi` and `web` write the endpoint's flash before they change anything in use. When the flash
cannot be written they answer `storage-failed` and keep the running credentials, token and
connection as they were. The flash has no transactions, so part of the setting may be stored; set it
again. Nothing is retried on its own.

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
unsupported and incompatible requests receive explicit error status. Every status other than an
operation's success status is answered before anything is emitted, so the host treats it as a
refusal. Boot diagnostics lack a valid matching identity and cannot complete a host operation. The
host's command library and scenes exist only on the host. Built-in remotes are fixed at build time;
the endpoint has no operation that adds, changes or stores commands.
