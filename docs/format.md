# Open Issue project and issue format

## Project files and isolation

`issue init` creates:

```text
.issues/
  config.json
  next-id
  issues/
```

`config.json` contains the `formatVersion` integer. Version `1` is supported; unknown versions fail without rewriting data. Initialization refuses to overwrite an existing `.issues` path.

Issue commands locate the closest `.issues/config.json` walking upward from the current working directory. This makes the command available from any project subfolder. It always operates on the issue store rooted at that marker; it has no option to point an existing project command at another issue store. If another project is initialized inside a subfolder, commands run there use the nested project and cannot read or mutate its parent project’s issue store. Legacy `.free-issue` directories are not discovered or automatically migrated; they are left untouched.

## Issue Markdown

Each issue is stored as `.issues/issues/<id>.md`, UTF-8 Markdown with a YAML frontmatter mapping between `---` lines, followed by the Markdown body. IDs are positive, monotonically allocated integers, immutable within the project, and independent of titles. The `next-id` high-water mark and a cross-process allocation lock prevent ID reuse after deletion and ensure creating an issue does not overwrite a previous issue.

| Key | Type | Rules |
| --- | --- | --- |
| `schema` | integer | Must be `2`. |
| `id` | integer | Positive numeric ID and equal to the filename. |
| `title` | string | Nonempty, single line. |
| `status` | string | `open`, `closed`, or `declined`. |
| `ready` | boolean | Defaults to false (draft). |
| `priority` | string | `low`, `normal`, `high`, or `urgent`. |
| `tags` | list of strings | Defaults to empty; entries must be nonempty and unique ignoring case. |
| `created_at` | timestamp | UTC ISO 8601; immutable. |
| `updated_at` | timestamp | UTC ISO 8601; refreshed on edit. |
| `parents` | list of integers | IDs of parent issues. |
| `children` | list of integers | IDs of child issues. |
| `related` | list of integers | IDs of related issues. |
| `blocks` | list of integers | IDs of issues blocked by this issue. |

Relationship lists are explicit, support multiple links at every level, and do not require reciprocal entries. Every referenced ID must exist. Self-links and parent/child or blocking cycles are rejected on mutation. Broken references in manually edited files produce diagnostics without deleting issue data. Unknown frontmatter fields are retained when updating.

Example:

```yaml
---
schema: 2
id: 1
title: Document the public API
status: open
ready: false
priority: normal
tags:
- docs
created_at: "2026-10-03T20:00:00.0000000+00:00"
updated_at: "2026-10-03T20:00:00.0000000+00:00"
parents: []
children: []
related: []
blocks: []
---
# Context

Why this work is useful.

# Desired outcome

What should be true when it is complete.

# Acceptance criteria

- [ ] Public operations are documented.
```

New issues start as drafts (`ready: false`). In the desktop editor, unchecking Draft marks an issue ready. Context, Desired outcome, and Acceptance criteria are starter headings, not required for a draft. A ready issue must be open and have a nonempty Acceptance criteria section. Readiness authorizes selection by `next`, not autonomous implementation.

## Reads and writes

Files are never rewritten on read. Malformed issue files are reported individually; other valid issues remain available. IDs are validated before path construction. Symlinks/reparse points for the project data directory, issue directory, and issue files are rejected. Markdown is not executed or treated as trusted HTML.

Writes use a temporary file in the issue directory, flush it, and move it into place. Per-issue exclusive locks coordinate writers; issue creation also takes a project-wide allocation lock. An update checks the caller’s SHA-256 revision before preparing and immediately before replacing the file, reporting conflicts instead of overwriting manual/concurrent edits. Manual edits should preserve YAML validity, the filename/ID match, allowed values, and numeric references.

## Ordering and selection

Lists are ordered by status (`open`, `closed`, `declined`), priority descending (`urgent`, `high`, `normal`, `low`), creation time ascending, then numeric ID ascending. `next` considers open, ready, unblocked issues and uses priority descending, creation time ascending, then numeric ID ascending. It reports an issue; it does not start work.
