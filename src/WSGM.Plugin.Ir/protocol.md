# IR endpoint protocol 1

Transport: UTF-8 JSON objects, one compact line per frame, LF terminated, optional CR ignored.
Maximum frame length is 32768 bytes; maximum JSON nesting is eight on the endpoint. No network
transport is exposed by this firmware. Every request carries integer `v: 1`, a nonempty string `id`
of at most 64 characters and `op`. Replies echo `v` and `id`, with `status` and optional `data`.
Only matching request identities may complete host work. The host never retries an uncertain write.

| Operation             | Request fields                      | Successful reply                                                          |
| --------------------- | ----------------------------------- | ------------------------------------------------------------------------- |
| `identify` / `health` | none                                | `ok`; identity, model, firmware, protocol, maxTimings, learning, uptimeMs |
| `learn`               | `timeoutMs`: 1000–30000             | `learned`; data contains a self-contained payload                         |
| `cancel`              | none                                | `ok`; outstanding learn also receives `cancelled`                         |
| `send`                | payload, repeats: 0–4, gapMs: 0–200 | `transmitted`, confirming emission only                                   |

`learn` is asynchronous on the endpoint so cancellation and health remain available. Another learn
or send while learning receives `busy`. Learning also ends on its firmware deadline without a host.
Send is bounded to five seconds including repeats and gaps. Cancellation of a host send wait does
not imply that emission stopped. There is no automatic replay after reconnect.

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
