# Open Issue: implementation plan

## Goal and boundaries

Build a local-first issue and specification manager whose canonical data is plain Markdown files in a project directory. It must work without a network connection, Git repository, account, hosted service, or database. Users interact through a CLI and a Windows desktop application; agents interact through documented CLI output and, later, a local tool interface. The tool manages issues and specifications, **not** source-control operations or an LLM service.

The first supported system is Windows desktop. Use a portable .NET core and CLI so a different desktop frontend can be added for other operating systems later. Do not promise cross-platform desktop support in the first release.

## Product decisions for the first release

- Product name: **Open Issue**; executable: `issue`, installable as a .NET global tool. Use System.CommandLine for CLI parsing; evaluate terminal formatting separately.
- An issue is a specification-capable Markdown document. The first release supports `open`, `closed`, and `declined`; `declined` covers closing without implementation. `ready` is an explicit boolean independent of status. `ready` authorizes *selection*, not autonomous implementation.
- Windows UI: WPF using the shared application core. This is a Windows-only presentation layer, not a reason to put business rules or filesystem logic in the UI. Evaluate a separate cross-platform frontend only after the core and format are stable.
- Prefer supported .NET SDK/runtime and first-party libraries where practical; select and pin the exact versions when implementation starts. Review the licensing, maturity, offline restore/distribution, and security of any additional packages before adoption.
- The core and CLI are the authority for behavior; the UI and any agent adapter must call the same core rather than parse or edit files independently.

## Storage contract (specify and test before building the UI)

Project discovery walks upward from the current directory until it finds `.issues/config.json`. Commands operate only on the nearest initialized project and are available from any descendant directory; commands cannot point at another project. Do not inspect Git metadata. `issue init` creates `.issues/config.json` and `.issues/issues/`; it refuses to overwrite existing data. Configuration records a format version and only project-wide options actually needed. Unrecognized future format versions fail with an actionable error rather than being rewritten.

Each issue lives at `.issues/issues/<id>.md`, with UTF-8 text, YAML frontmatter, and a Markdown body. Allocate positive numeric immutable IDs under a cross-process allocation lock; use each ID as its filename and never derive it from the title. At minimum, frontmatter has `schema`, numeric `id`, `title`, `status`, `ready`, `priority`, `tags`, `created_at`, `updated_at`, and lists of numeric `parents`, `children`, `related`, and `blocks`. Use UTC ISO 8601 timestamps. Define allowed values (`status`: open/closed/declined; `priority`: low/normal/high/urgent) and deterministic sorting. Preserve unknown frontmatter keys on update so newer metadata is not silently discarded. Preserve the Markdown body and line endings when changing metadata where feasible; do not rewrite files on read.

Provide a documented starter template with sections for Context, Desired outcome, and Acceptance criteria; allow freeform Markdown rather than requiring every section for a draft. A ready issue must be open and have a nonempty acceptance-criteria section. At creation, body and title are user-supplied; define an editor fallback for interactive use and a noninteractive `--title`/`--body-file` route for scripts. Reject empty titles and invalid metadata with file-specific diagnostics. Treat malformed issues as errors for that issue while allowing other valid issues to be listed.

References use stable numeric IDs, not filenames or titles. Support lists of `parents`, `children`, `related`, and `blocks`; reject self-references, nonexistent IDs, and parent/child or blocking cycles on mutation; report existing broken references without deleting data. Milestones/epics/projects may later build on the issue relationship format. Document how manual edits are validated.

Do not expose arbitrary filesystem paths through IDs or references. Resolve issue paths only from validated IDs within the project issue directory; define a policy for symlinks and reject paths that escape the project. Treat Markdown as untrusted for rendering: disable raw HTML or sanitize it, and do not execute embedded commands. Limit UI binding to loopback if a local web UI is ever introduced.

Writes must be atomic on the same filesystem: write and flush a temporary file in the issue directory, then replace/move it into place; clean up failed temp files. Serialize competing writers for an issue across processes, compare an expected content revision before writing, and surface conflicts rather than losing manual edits. Creating a new issue must not overwrite an existing file. Test interrupted writes, concurrent edits, malformed YAML, and unsupported schema versions. A cached search index is optional but must be disposable; Markdown files remain the source of truth.

## Application architecture

1. **Domain and storage library**: project discovery, schema/validation, parsing/serialization, safe file access, atomic persistence, and revision conflict detection. No UI, Git, web, or agent dependencies.
2. **Application service**: create/read/update/list/search, status and readiness transitions, relationships, and deterministic `next` selection. Return typed errors and diagnostics shared by all interfaces.
3. **CLI**: thin adapter exposing human-readable output and stable machine-readable JSON (`--json` for read commands; structured errors and documented exit codes). Do not make agents scrape formatted tables.
4. **Windows desktop UI**: WPF project using the application service, with a left issue list and right details/editor panel, status/tag/priority/readiness filters, search, and conflict/error feedback. Run entirely locally.
5. **Agent adapter**: local stdio MCP server if its protocol and dependency choices pass review; otherwise the JSON CLI remains the baseline agent interface. Expose narrowly scoped list/read/search/create/update operations. Agent-driven `plan` and `implement` are separate, opt-in integrations and are not required for the offline issue manager.

## CLI contract

Start with `issue init`, `new`, `list`, `show <id>`, `edit <id>`, `close <id>`, `decline <id>`, `reopen <id>`, `search <query>`, `next`, and `help`. Add `ready <id>` / `unready <id>` and relationship editing via explicit commands or documented `edit` fields. `list` filters by status (including closed/declined), tag, priority, and readiness; tags can be enumerated without a second database. IDs are positive integers. `next` selects the highest-priority open, ready, unblocked issue using a documented stable tie-breaker; it does not start work. Default search is case-insensitive text over title, tags, and body; exact and regex search are later enhancements after query semantics and safe regex timeouts are defined. Commands must not prompt when input/output is redirected unless explicitly requested.

## Sequenced Luna-agent work packages

Each package should be a separate, reviewable change. The assigned agent must read the preceding contract and existing tests, implement only its package, run the repository's existing relevant checks, and record any contract change for the next package. Do not start a dependent package before its prerequisite is merged and verified.

### A. Format and project skeleton

**Depends on:** none. Establish the .NET solution layout (core, CLI, Windows desktop, tests), pinned SDK policy, file-format documentation, template, and representative fixture files. Define exact frontmatter types, discovery behavior, ID rules, error categories, and migration policy in documentation before implementing persistence.

**Done when:** a human can create a valid issue file by hand from the specification; fixtures cover valid, malformed, unknown-version, and linked issues; solution builds on the targeted Windows environment. Do not add a database or a Git requirement.

### B. Filesystem repository and validation

**Depends on:** A. Implement discovery, parsing, validation, safe path handling, reading, atomic create/update, revision checks, and cross-process write coordination. Keep Markdown readable with ordinary editors.

**Done when:** tests cover offline use outside a Git repository, UTF-8 and line endings, duplicate/malformed files, schema incompatibility, unknown-field preservation, manual edits during update, concurrent writes, symlink/path escape, and interrupted-write cleanup. An invalid file never causes valid issues to disappear or be overwritten.

### C. Application operations

**Depends on:** B. Implement status/ready transitions, tags and priority, list/filter/search, relationships, and `next` on the common service. Define diagnostics for broken references and cycles.

**Done when:** tests show deterministic ordering and filtering; readiness and blocking rules are enforced; changes preserve unrelated content; an issue cannot be selected merely because an agent asks for the next one if it is blocked or not ready.

### D. CLI and machine interface

**Depends on:** C. Implement commands and help above, interactive editor behavior, scripted input, JSON output, consistent exit codes, and package/run instructions for Windows.

**Done when:** end-to-end tests exercise creation through close/decline/reopen, ambiguous IDs, no-project and invalid-file errors, and parseable JSON without progress text on stdout. All basic workflows work with networking unavailable.

### E. Windows desktop

**Depends on:** C and stable CLI/format contract from D. Implement a keyboard-accessible WPF list/detail/editor with create, update, ready, close/decline/reopen, filters, search, and validation/conflict messages. Read fresh data when the window regains focus or files change; provide an explicit refresh fallback. Never silently discard unsaved edits.

**Done when:** manual Windows acceptance checks cover editing the same issue in a text editor and UI, conflicts, large issue lists, invalid Markdown/frontmatter, keyboard navigation, and offline startup. UI actions produce files indistinguishable from CLI actions.

### F. Agent tools and optional automation

**Depends on:** D; E is not required. Document safe agent instructions for finding the project and invoking JSON CLI operations. Evaluate MCP over stdio and, if adopted, map its tools directly to application operations with clear input schemas and errors. Keep user confirmation and issue readiness separate from an agent's permission to modify source code; do not bundle an autonomous coding agent or remote model dependency.

**Done when:** a local agent can list, read, search, create, and update an issue without a network service; malformed inputs cannot access arbitrary paths; implementation does not begin on `next` or `ready` alone.

### G. Release and future-platform assessment

**Depends on:** D and E; F may be shipped separately. Package the Windows CLI and desktop app with reproducible installation instructions, document how to back up/move the plain files and handle format upgrades, and validate clean-machine offline operation. Assess a cross-platform desktop frontend against the now-stable core/API rather than coupling storage to WPF.

**Done when:** Windows release checks pass without account or Git setup and without network access after installation. Cross-platform work has an explicit frontend choice and test matrix, but is not represented as shipped.

## Release acceptance and deferred work

MVP is complete after A–E: an offline Windows user can create, inspect, edit, find, link, prioritize, mark ready, and resolve Markdown issues with either CLI or desktop UI, and changes remain portable between directories and editors. F adds agent-facing convenience without changing the source-of-truth format.

Defer VS Code integration, milestones, file links, fuzzy/regex search, agent-generated plans or implementations, and non-Windows desktop packaging until core workflows and user feedback justify each one. Later adapters must reuse the shared service and honor the same storage and conflict rules.
