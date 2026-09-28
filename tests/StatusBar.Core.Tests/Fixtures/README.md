# Test fixtures

Fixtures here must be **sanitized or hand-written**. Never commit real prompts, transcripts,
file paths, account identifiers or tokens.

- `codex/` — minimal Codex rollout JSONL files (one scenario per file).
- `cowork/` — minimal Claude Cowork `local_<id>.json` metadata and `audit.jsonl` files.

Structural samples captured by `tools/recon/Collect-Recon.ps1` (which redacts all free text)
are the preferred starting point. Name files after the scenario they prove, for example
`codex/turn-pending-approval.jsonl`.
