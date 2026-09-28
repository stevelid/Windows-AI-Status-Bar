# Windows AI Status Bar

A small, always-available Windows utility that provides an ambient overview of currently running AI work across the ChatGPT/Codex and Claude desktop applications.

The collapsed strip sits immediately above the Windows taskbar and answers three questions at a glance:

1. How much Codex and Claude allowance is left?
2. How many AI tasks are running?
3. Does anything need my attention?

```
GPT 62%  Claude 41%  ● 3  ⚠ 1
```

Clicking the strip expands a small pane with reset times and the list of tasks.

## Status

Under development. See [`docs/PLAN.md`](docs/PLAN.md) for the implementation plan, [`docs/PROGRESS.md`](docs/PROGRESS.md) for progress, [`docs/BRIEF.md`](docs/BRIEF.md) for the product brief and [`docs/AGENT_PROMPT.md`](docs/AGENT_PROMPT.md) for the prompts that drive the implementation agent. Phase 0 steps for Steve are in [`docs/recon/README.md`](docs/recon/README.md).

## Origin and licence

This project is a fork of [Kilin-570/AIUsageWidget](https://github.com/Kilin-570/AIUsageWidget) (MIT). The upstream history is preserved in this repository and the upstream README is kept at [`docs/UPSTREAM_README.md`](docs/UPSTREAM_README.md). The original MIT licence is retained in [`LICENSE`](LICENSE).
