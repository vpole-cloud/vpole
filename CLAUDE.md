@AGENTS.md

## Claude Code specifics

`AGENTS.md` above is the canonical project guide; this section only maps its tool-neutral wording to Claude Code.

- **Skills** live in `.claude/skills/<name>/SKILL.md`. Invoke them with the Skill tool when the task matches their description; the user can run them as `/<name>` (for example `/feature 123` or `/fix-bug 456`).
- **Built-in agents** means the Agent tool. Use `Explore` for broad read-only searches, `Plan` for architecture planning and `general-purpose` for independent review or bounded implementation. Do not add persistent personas under `.claude/agents/`.
- **Separate worktrees** for writers: use the Agent tool with `isolation: "worktree"`, or `git worktree add`. Run at most three delegated agents at a time, and continue an existing agent with SendMessage when its context is still useful.
- **Local plans and task notes** go in `.claude/notes/`, which is ignored.
- **Aspire MCP**: `.mcp.json` registers `aspire agent mcp --non-interactive`; `.claude/settings.json` enables it and pre-approves its read-only tools. Personal overrides belong in the ignored `.claude/settings.local.json`.
