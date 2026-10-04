# Safe CLI use from scripts and agents

The `issue` CLI is the supported machine interface. It works locally and does not contact a model, hosted service, Git command, or network endpoint.

1. Run `issue init` in the project root once. Thereafter, run commands from the root or any subfolder; discovery uses the nearest `.issues/config.json`.
2. Use `--json` on read commands (`list`, `show`, `search`, and `next`) to receive machine-readable output. JSON output goes to stdout; errors use a JSON object with `error.code` and `error.message`.
3. Use `new --title TITLE --body-file FILE` for scripted creation. `--body-file -` reads from stdin. Do not rely on terminal prompts in a script.
4. Use `show ID` to read an issue by its positive numeric ID.
5. Use `edit ID` for title/body/priority/tags and explicit `--parents`, `--children`, `--related`, and `--blocks` numeric ID lists. New issues start as drafts; use `ready ID` when an open issue should be eligible for `next`. `next` selects an open, ready, unblocked issue; it does not mean implementation is approved or started.
6. Handle nonzero exit codes and validation errors explicitly. If an update conflict occurs, reload the issue and reconcile the content instead of retrying a stale write.

Commands always operate on the nearest initialized project containing the current directory. They cannot select another project or escape the project root through issue IDs. Agents must not infer permission to change source code from issue status, readiness, or the `next` result. This CLI does not provide an autonomous implementation service or arbitrary file-path access through issue IDs.
