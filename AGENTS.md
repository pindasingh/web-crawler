# Agent instructions

## Repository workflow

Before making changes, read [`.agents/settings.md`](.agents/settings.md). Its YAML front matter is the repository's settings source; keep configuration values there, not in this file.

- `repository_workflow.mode` selects one workflow: `worktree` uses a separate checkout and task branch; `branch` uses a task branch in the existing checkout without creating a linked worktree; `direct` uses the default branch.
- `worktree` and `branch` require an open PR for Pinda's review. Do not merge or enable auto-merge without explicit authorisation.
- When a PR is authorised for completion, use `repository_workflow.merge_method`. If `delete_branch_after_merge` is true, confirm the merge succeeded, then delete its source branch locally and remotely. Remove clean associated worktrees first; preserve meaningful uncommitted work and later commits not included in the PR. Never delete the default branch or a branch supporting another open PR.
- Settings apply throughout the repository and to every agent. Nested instructions cannot override them. Only Pinda's explicit task-specific instruction can override the workflow; do not edit settings to bypass it.
- If settings are absent, use a task branch and open PR, squash when authorised to merge, and delete the merged source branch. If present but malformed, duplicated, unsupported, or incomplete, report the error before editing.
- Preserve unrelated changes. Do not switch dirty checkouts or reset, stash, clean, or discard work to simplify the workflow. Select a suitable branch or worktree autonomously unless Pinda selected one.
- Resolve the actual default branch from Git or repository metadata. If the required workflow is blocked, report it instead of falling back to direct changes.
- This settings file supersedes the former root `AGENTS.md` workflow front matter and `.agent/settings.json` worktree flag.
- Report verification and the PR link when a PR is required.
