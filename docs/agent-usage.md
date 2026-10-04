# Safe CLI use from scripts and agents

The `issue` CLI is the supported machine interface. It works locally and does not contact a model, hosted service, Git command, or network endpoint.

1. Run `issue init` in the project root once. Thereafter, run commands from the root or any subfolder; discovery uses the nearest `.issues/config.json`.
2. Use `--json` on read commands (`list`, `show`, `search`, and `next`) to receive machine-readable output. JSON output goes to stdout; errors use a JSON object with `error.code` and `error.message`.
3. Use `new --title TITLE --body-file FILE` for scripted creation. `--body-file -` reads from stdin. Do not rely on terminal prompts in a script.
4. Use `show ID` to read an issue by its positive numeric ID.
5. Use `edit ID` for title/body/priority/tags and explicit `--parents`, `--children`, `--related`, and `--blocks` numeric ID lists. New issues start as drafts; use `ready ID` to mark an issue ready. Readiness is independent of status, but `next` selects only open, ready, unblocked issues; it does not mean implementation is approved or started.
6. Use `delete ID` to permanently remove an issue that no other issue references. Remove incoming relationship links before deleting a referenced issue; numeric IDs are not reused.
7. Handle nonzero exit codes and validation errors explicitly. If an update conflict occurs, reload the issue and reconcile the content instead of retrying a stale write.

Commands always operate on the nearest initialized project containing the current directory. They cannot select another project or escape the project root through issue IDs. Agents must not infer permission to change source code from issue status, readiness, or the `next` result. This CLI does not provide an autonomous implementation service or arbitrary file-path access through issue IDs.
