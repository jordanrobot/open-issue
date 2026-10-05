# Product

<!-- impeccable:product-schema 1 -->

## Platform

Repository-derived, not user-confirmed: Windows desktop (WinUI 3) with a portable .NET CLI. The current Impeccable platform values do not include Windows desktop, so the category remains unresolved rather than being mapped inaccurately to `web` or `adaptive`.

## Users

Inferred from repository documentation; not user-confirmed: software developers and repository maintainers who need to track implementation work and specifications alongside project files. The documented workflows also support scripts and coding agents as clients.

## Product Purpose

Inferred from repository documentation; not user-confirmed: Open Issue keeps project-specific issues and specifications in plain Markdown files, accessible through a CLI and a Windows desktop editor. Its documented aim is to make issue management available without requiring an account, network service, database, or Git repository, while keeping project data local and user-owned.

## Positioning

Inferred from the documented implementation; not user-confirmed or independently market-validated: the issue store is a project-local `.issues` directory of Markdown files, and the CLI and Windows desktop app share the same core and storage contract. The CLI provides JSON output for automation. Do not present these mechanics as a validated competitor comparison.

## Operating Context

Repository-derived, not user-confirmed:

- A user initializes an issue project in a directory; commands run from that directory or its subdirectories discover the nearest `.issues/config.json`.
- Work can happen from the terminal, automation, or the Windows desktop editor.
- New issues begin as drafts. The `next` command reports an eligible open, ready, unblocked issue; it does not start implementation or grant permission to change source code.
- Issue data is Markdown with structured metadata and explicit numeric relationships.

## Capabilities and Constraints

Repository-documented; not separately user-confirmed:

- The core and CLI target .NET 10; the desktop application uses WinUI 3 and the Windows App SDK.
- The CLI supports issue creation, listing, search, display, editing, readiness, deletion, and next-issue selection. Read commands can return JSON for scripts and agents.
- Issues are stored as UTF-8 Markdown under `.issues/issues/`. Numeric IDs are allocated per project and are not reused after deletion. The format supports status, draft/readiness, priority, tags, and parent, child, related, and blocking relationships.
- Commands use the nearest initialized project and do not accept an arbitrary issue-store path override. Invalid files are reported as diagnostics without hiding other valid issues; writes use revisions to detect conflicts, and unsafe IDs and symlinks/reparse points are rejected.
- The desktop UI is Windows-specific. The portable core and CLI do not use WinUI APIs.
- The README documents no account, network service, database, or Git repository requirement. Windows desktop usage has additional Windows App SDK/runtime requirements.
- Readiness is a selection signal for `next`, not authorization for autonomous implementation. Destructive deletion is permanent and blocked while another issue references the target.

## Brand Commitments

Repository evidence identifies the product as Open Issue. No additional user-confirmed voice or brand commitments were established during this init.

## Evidence on Hand

- [README.md](./README.md) describes the product, installation, basic workflows, and build verification.
- [docs/format.md](./docs/format.md) documents the project and issue format, relationships, and storage behavior.
- [docs/agent-usage.md](./docs/agent-usage.md) documents safe CLI use by scripts and agents.
- [docs/release.md](./docs/release.md) documents Windows distribution and runtime requirements.
- The repository contains example issue data and executable core regression checks.
- No customer research, testimonials, or independently validated market-positioning evidence is documented here. Do not invent any.

## Product Principles

Inferred from documented behavior; not user-confirmed:

1. Keep project data local, portable, and human-readable.
2. Make the same issue store usable from both the CLI and the Windows editor.
3. Treat IDs, relationships, revisions, and user-authored metadata as data-integrity concerns.
4. Keep automation explicit and user-controlled; issue readiness must not be mistaken for implementation approval.
