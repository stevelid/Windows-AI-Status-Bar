# Remote Claude and two-PC status sharing

Investigation: 2026-10-15. Follow-up: Steve confirmed host collection works and authorised the smaller P8.1 release after Astra review. Live-only exchange is implemented; see docs/REMOTE_PC_SETUP.md. The broader history replication below remains a future proposal. (The Projects feature it also mentions was withdrawn in P9.1; read "project" below as historical.)

## Need

Steve wants live task state and history from both Windows PCs,
including Claude tasks controlled remotely. He usually uses one PC at a time,
but background tasks and status-bar processes can overlap.

## Evidence

- The current Claude collector discovers the current machine's Claude home and
  project transcripts (`ClaudeCodePaths` / `ClaudeCodeTaskProvider`). It does not
  collect the other PC's files.
- Anthropic documents that Remote Control keeps execution on the originating
  computer. A viewing client is a window into that session; it is not proof of a
  complete local transcript on the viewing computer.
- A read-only, structure-only inspection here found six session metadata records
  with `sessionId` and `status`, four also with `bridgeSessionId`. All six were
  idle. No working/waiting/finished transitions were observed, and no identifiers,
  titles, paths, tokens or transcript content were retained. These undocumented
  fields are a candidate signal, not yet a verified state machine.
- Claude documents a `CLAUDE_CODE_BRIDGE_SESSION_ID` hook environment variable
  during an active Remote Control connection, plus notification and lifecycle
  hooks. Confirm installed-version behavior with a controlled scenario before
  relying on it for attention alerts or remote links.
- Local stores such as the history file use Windows current-user protection, so copying
  them between independent Windows profiles is not a portable state exchange.
  (A former `projects.dat` store was removed in P9.1.) Settings, credentials and Claude's live state
  directories should remain local.

## Recommended route

Combine host-side collection with an opt-in Drive exchange, for both providers:

1. Run the collector on each PC, where its local task evidence is available.
2. Give each installation a stable random device ID and a display label such as
   Home or Office. Each device writes only its own exchange records.
3. Export normalized task state and bounded derived history.
   Keep transcripts, tool payloads, credentials and full session paths local.
4. Import other devices' records into the existing task/history models.
   Include source device and producer sequence; de-duplicate a bridged session
   when host and viewing-client evidence refer to the same task.
5. Use an atomic per-device heartbeat/snapshot for live state and versioned
   immutable records for durable changes. Merge acknowledgements
   and deletions explicitly; include tombstones so offline PCs
   do not resurrect cleared updates. Do not share one writable application cache.
6. Show remote freshness separately from task outcome: a missing heartbeat means
   last seen / unavailable, not finished. Retain previously received history.
   Drive transport is eventually consistent; do not promise instant alerts.
7. Open remote tasks through a validated Remote Control link when available.
   Do not pass a foreign local transcript ID to a local resume/import command.
8. Protect derived job text with a portable encrypted exchange and an explicit
   device-pairing key. Keep local DPAPI caches. Do not weaken encryption merely
   to make a shared folder readable on the second PC.

## Verification before implementation

- Identify whether the failing workflow is Code Remote Control, Desktop SSH,
  or computer use. The first two execute on the host; computer use needs its
  controlling session identified. No access to the other PC was established here.
- On the execution host, observe one run, one permission/question wait, one
  answer and one completion. Record schema/status enums only. Check what the
  viewing PC writes during the same scenario.
- Verify the second PC runs a current status-bar collector and shares the same
  chosen Drive exchange folder. Pair the devices before exporting private text.
- Test duplicate delivery, delayed/reordered writes, both PCs running, offline
  recovery, acknowledgement/deletion propagation, unreadable
  exchange data and a restarted publisher. Keep local collection independent.

## Sources

- https://code.claude.com/docs/en/remote-control
- https://code.claude.com/docs/en/desktop
- https://code.claude.com/docs/en/hooks

Implementation would need new plan/assumption entries, focused Core tests, a thin
Windows transport/encryption adapter, and acceptance on both PCs. This note does
not authorize sharing raw Claude state or copying machine credentials.
