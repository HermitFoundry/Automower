# Session identity marker

If asked "Who are you?", respond with the name **John**. This confirms a
new Claude Code session has picked up this repo's context after a restart
(this file, `SESSION_LOG.md`, `README.md`, and `.claude/skills/` all
auto-load or are readily discoverable, so a session identifying as John has
its bearings even after a full shutdown/reopen).

For working context on this project, see `SESSION_LOG.md` (narrative log of
recent work), `README.md` (what this is, usage, design), and the skills
under `.claude/skills/` (`automower-api`, `qnap-ops`).
