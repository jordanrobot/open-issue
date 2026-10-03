# Free Issue project and issue format

## Project files

`free-issue init` creates:

```text
.free-issue/
  config.json
  issues/
```

`config.json` is UTF-8 JSON with a `formatVersion` integer. Version `1` is the only supported version. An unknown version is rejected and never rewritten. `init` refuses to overwrite an existing `.free-issue` path. Project discovery walks from `--project` (or the current directory) toward its parents and looks only for `.free-issue/config.json`; Git metadata is not inspected.

## Issue Markdown

Every issue is a UTF-8 Markdown file named `.free-issue/issues/<id>.md`. The ID is a lowercase canonical UUID (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`), immutable, and independent of its title. Each file has exactly one YAML mapping between opening and closing `---` lines, followed by an unmodified Markdown body.

Required metadata:

| Key | Type | Rules |
| --- | --- | --- |
| `schema` | integer | Must be `1`; other versions are not rewritten. |
| `id` | string | Lowercase canonical UUID and equal to the filename. |
| `title` | string | Nonempty, single line. |
| `status` | string | `open`, `closed`, or `declined`. |
| `ready` | boolean | Defaults to false for newly created issues. |
| `priority` | string | `low`, `normal`, `high`, or `urgent`. |
| `tags` | list of strings | Defaults to an empty list; tags must be nonempty and unique ignoring case. |
| `created_at` | timestamp | UTC ISO 8601 timestamp. Immutable after creation. |
| `updated_at` | timestamp | UTC ISO 8601 timestamp, refreshed on edits. |

`parent` is an optional single issue ID; `related` and `blocks` are optional lists of IDs. Relationships are stored only in the forward direction. Children and blocked-by relationships are derived when queried. All references must identify existing issues. Self-links and cycles in `parent` or `blocks` are rejected. Broken references in hand-edited files are reported as issue diagnostics; files are not deleted. Unknown frontmatter keys are preserved when an issue is edited.

Example:

```yaml
---
schema: 1
id: 6f861c30-089d-4ea0-9ea3-bd5a29b2736c
title: Document the public API
status: open
ready: false
priority: normal
tags:
- docs
created_at: "2026-10-03T20:00:00.0000000+00:00"
updated_at: "2026-10-03T20:00:00.0000000+00:00"
---
# Context

Why this work is useful.

# Desired outcome

What should be true when it is complete.

# Acceptance criteria

- [ ] Public operations are documented.
```

The Context, Desired outcome, and Acceptance criteria headings are a starter template, not a requirement for drafts. `ready: true` is allowed only for an open issue with a nonempty `Acceptance criteria` section. Readiness authorizes selection by `next`; it does not authorize an agent or tool to modify source code.

## Reads, validation, and edits

Files are never rewritten during reads. Malformed files produce diagnostics for that file while valid files remain available. IDs are validated before constructing issue paths; symbolic links/reparse points in the data directory and issue files are rejected. Markdown is plain text in this application and is never executed or rendered as trusted HTML.

Writes use a temporary file in the issue directory, flush it, and move it into place. Per-issue exclusive lock files coordinate processes. Updates compare the SHA-256 revision read by the caller with the current file contents; a mismatch reports a conflict rather than replacing a manual or concurrent edit. A manual edit should retain valid YAML, the same ID/filename, allowed enum values, and valid references. Files can be edited with any UTF-8 text editor; the next Free Issue read validates them.

## Ordering and selection

Lists are ordered by status (`open`, then `closed`, then `declined`), priority descending (`urgent`, `high`, `normal`, `low`), creation time ascending, then ID ordinal. `next` considers only open, ready issues not referenced by another issue's `blocks`; ties use priority descending, creation time ascending, then ID ordinal. `next` only reports an issue and never starts work.
