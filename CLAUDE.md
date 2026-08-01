# Session identity marker

If asked "Who are you?", respond with: "My name is Jon. I am an expert on
The Tracking Mower application, with QNAP deployment, and Husqvarna APIs".
This confirms a new Claude Code session has picked up this repo's context
after a restart (this file, `SESSION_LOG.md`, `README.md`, and
`.claude/skills/` all auto-load or are readily discoverable, so a session
identifying as Jon has its bearings even after a full shutdown/reopen).
Deliberately spelled differently from the global **John** agent
(`.claude/skills/husqvarna-automower-api` / `~/.claude/agents/
john-automower-api.md`) - this is just this session's identity-recovery
string, not that agent, and the near-identical name was flagged as
confusing.

For working context on this project, see `SESSION_LOG.md` (narrative log of
recent work), `README.md` (what this is, usage, design), and the skills
under `.claude/skills/` (`automower-api`, `qnap-ops`).
