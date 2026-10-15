# Live status on both PCs

Install the shared build on both Windows PCs. Each status bar continues collecting
its own Claude and Codex sessions; the Other PC section displays the other machine's
last received tasks in the details pane. Local history remains local.

## Setup

The prepared private Drive bundle contains `AIStatusBar.exe`, `release.json`,
`Install-StatusBar.ps1`, `Install-StatusBar.cmd` and a `feed` folder.

1. Let Google Drive finish syncing this bundle to the other PC.
2. Double-click `Install-StatusBar.cmd` there. It checks the app checksum,
   installs into local AppData, keeps existing settings, and enables sharing.
3. Keep Drive and the status bar running on both PCs. The first received snapshot
   is unconfirmed; another advancing heartbeat establishes freshness.

In Settings > Other PC you can change this PC's display name or select the shared
`feed` directory if Drive is mounted elsewhere. Leave the shared-folder field blank
to turn sharing off. Existing Windows startup preferences are preserved; if enabled,
the app refreshes its startup shortcut to the installed executable.

## What is shared

- Bounded task title, task/provider identity, status and confidence, activity time,
  device label and an increasing heartbeat sequence. No raw transcript, prompts,
  tools, credentials, full session paths, history or derived summaries.
- Cloud tasks are excluded because each PC already collects the account's cloud
  tasks. Imported tasks are never republished, saved to
  local history, or opened with local session navigation.
- Exchange payloads use AES-256-GCM with a per-message key wrapped for the recipient
  using RSA-OAEP-SHA256; the sender signs each envelope with RSA-PSS-SHA256.
  Private identity/key/sequence and pinned peer fingerprints are DPAPI-protected
  locally. Only public enrollment keys and encrypted snapshots live in Drive.

Selecting the same private folder enrols the devices advertising public keys there.
That folder is the trust boundary: give access only to your own computers. First
keys are pinned locally. A changed peer key is refused rather than silently trusted;
device replacement needs deliberate re-enrollment. Never copy one PC's local identity
file to another. Deleting that file creates a new device identity.

## Timing and limitations

- Each PC publishes/checks every 15 seconds, plus Drive's variable sync delay.
- Duplicate or older sequences never refresh liveness. Receiver time controls
  freshness, so a different clock on the host cannot keep an old task live.
- After two minutes without an advancing snapshot, remote rows become Unknown
  and stop contributing working/attention counts. They do not become Complete.
  The last received rows remain visible for up to an hour in this app run.
- A cached file first seen after restart stays unconfirmed until the host advances
  it. Encrypted identity loss, malformed files, partial deliveries, changed keys
  or an unavailable folder do not stop local collection.
- At most eight peers and 100 tasks per sender. No remote notification balloons,
  dismissal, resume links, or history sync in this first version.
- Both PCs must install the build. Installing on one cannot update a collector
  running on the other PC. End-to-end Drive latency and live host acceptance need
  the other PC; a two-device filesystem simulation is covered by Windows smoke tests.
