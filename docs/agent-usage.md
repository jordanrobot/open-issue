# Safe CLI use from scripts and agents

The CLI is the supported machine interface. It works locally and does not contact a model, hosted service, Git command, or network endpoint.

1. Locate a project by running a command from the project directory, or pass `--project <directory>`.
2. Use `--json` on read commands (`list`, `show`, `search`, and `next`) to receive machine-readable output. JSON output goes to stdout; errors use a JSON object with `error.code` and `error.message`.
3. Use `new --title TITLE --body-file FILE` for scripted creation. `--body-file -` reads from stdin. Do not rely on terminal prompts in a script.
4. Use `show ID` to read a full issue. Full UUIDs and unambiguous prefixes are accepted.
5. Use `edit ID` for title/body/priority/tags and explicit parent/related/blocks fields. Use `ready ID` only after the acceptance criteria are meaningful. `next` selects an open, ready, unblocked issue; it does not mean implementation is approved or started.
6. Handle nonzero exit codes and validation errors explicitly. If an update conflict occurs, reload the issue and reconcile the content instead of retrying a stale write.

Agents must not infer permission to change source code from issue status, readiness, or the `next` result. This CLI does not provide an autonomous implementation service or arbitrary file-path access through issue IDs.
