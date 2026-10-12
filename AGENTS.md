---
repository_workflow:
  mode: branch
---

# Agent instructions

## Repository workflow

Read the YAML front matter in this repository's root `AGENTS.md` before making any repository change. `repository_workflow.mode` is one mutually exclusive choice:

| Mode | Required workflow |
| --- | --- |
| `worktree` | Create a separate Git worktree on a task branch, commit and push there, then open a PR against the default branch. |
| `branch` | Use a task branch in the existing checkout, commit and push there, then open a PR against the default branch. Do not create a linked worktree. |
| `direct` | Use the repository's default branch and commit and push directly to it. A task branch and PR are not required. |

- If the field is missing, use `branch`. If it is invalid or duplicated, report the configuration error before editing.
- Resolve the default branch from Git or repository metadata; do not assume it is named `main`.
- In `worktree` and `branch` modes, leave an open PR for Pinda's review. Do not merge, enable auto-merge, or commit or push to the default branch.
- Requests to save, update, record, or capture follow the configured mode. If a required branch or PR cannot be created, report the blocker; do not fall back to direct changes.
- Only Pinda's explicit instruction can override the mode for a specific task. Do not edit this configuration to bypass the selected workflow.
- This root setting applies throughout the repository and to every agent, including delegated agents. Nested instructions cannot change the workflow mode.
- This convention supersedes older mandatory-worktree and PR-only rules, including `worktrees.enabled` in `.agent/settings.json`. Keep unrelated settings there.
- Preserve existing modifications and untracked files. Do not switch a dirty checkout to another branch or reset, stash, clean, or discard unrelated work to make the workflow easier.
- Select a suitable task branch or worktree autonomously within the configured mode unless Pinda explicitly selected one. A repository change request authorises this routine selection.
- Report the result and verification; include the PR link when the mode requires a PR.
