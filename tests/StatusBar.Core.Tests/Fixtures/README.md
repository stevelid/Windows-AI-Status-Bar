# Test fixtures

Fixtures here must be **sanitized or hand-written**. Never commit real prompts, transcripts,
tool arguments, session file paths, account identifiers or tokens. Hand-written fixtures use
short synthetic placeholder values in the real fields so the parser contract is visible.

- `codex/` — minimal Codex rollout JSONL files (one scenario per file).
- `cowork/` — minimal Claude Cowork `local_<id>.json` metadata and `audit.jsonl` files.

Structural samples captured by `tools/recon/Collect-Recon.ps1` (which redacts all free text)
are the preferred starting point. Name files after the scenario they prove, for example
`codex/turn-pending-approval.jsonl`.

Until P0.4 recon is complete, parser scenarios are marked `provisional-`. Fixtures contain
structural fields and synthetic identifiers; placeholder message text is fabricated and never
copied beyond the short in-memory title or a terminal-question check.
