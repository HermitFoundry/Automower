---
name: qnap-ops
description: This skill should be used when running commands against the QNAP TS-673A NAS or the Debian containers on it that host this project (debian-dev1 for the hybrid-track daemons, AutomowerWeb for the public dashboard, AutomowerCaddy for the reverse proxy) - SSH access, docker exec, deployment/redeploy, or debugging why a remote command's quoting broke. Triggers on "qnap", "the container", "ssh automower", "docker exec", "redeploy", "startall.sh"/"startweb.sh" run remotely.
version: 1.0.0
---

# QNAP operations notes

Operational knowledge for driving `C:\repos\automower`'s QNAP TS-673A NAS
deployment **from a Claude Code session** - the commands and failure modes
that actually come up when running things there, not general infra
reference. For narrative/background depth (why ports are what they are, the
IPv6 security fix, the provisioning saga) see `docs/qnap_infrastructure_setup.md`
and `docs/deployment.md` - this file stays focused on "what to actually run
and what breaks."

## Topology

- **QNAP host** (`ssh automower-host`, alias for `terje@192.168.10.142`) -
  the NAS's own OS. `docker` is not on `PATH` for a non-interactive
  invocation here (see "docker not found" below).
- **`debian-dev1`** - the container running the 3 mowers' `hybrid-track`
  daemons in tmux (`automower-AM405X`, `automower-AM430X`,
  `automower-AM308V` sessions), bind-mounted at `/repos` (host path
  `/share/Repos`), working directory `/repos/Automower`. `ssh automower`
  (alias with `RemoteCommand` set to `docker exec` into this container)
  drops straight into a shell here.
- **`AutomowerWeb`** - separate container running the public dashboard
  (`startweb.sh`), same `/repos` bind mount as `debian-dev1` (**they share
  one git working tree, not two independent clones** - a `git checkout` in
  one is immediately visible in the other; only isolated at the
  process/port level, not the filesystem).
- **`AutomowerCaddy`** - reverse proxy/TLS termination, the only container
  with published host ports (`8880`→80, `8443`→443, forwarded from the
  internet by the router). Rarely needs touching.

Any dev/isolated container from earlier feature work (e.g. an
`Automower-dev` checkout) has been retired post-cutover (2026-07-30) - don't
assume one still exists without checking `docker ps -a` first.

## Two-hop access pattern

```
ssh automower-host                          # NAS OS itself
ssh automower-host -t "docker exec -it -w /repos/Automower debian-dev1 bash"
ssh automower                                # alias for the line above
```

Both aliases live in the **local machine's own** `~/.ssh/config` (Windows:
`C:\Users\<you>\.ssh\config`), not on the NAS. See `docs/qnap-access.md` for
the full alias block if it needs recreating.

## `docker: command not found`

`docker` is only on `PATH` for an *interactive login* shell on the QNAP
host - any non-interactive form (`ssh host -t "docker ..."`, an SSH config
`RemoteCommand`, this skill's own examples below) needs the full path:

```
DOCKER=/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker
```

Always wrap remote `docker` calls in `bash -lc "..."` (sources the login
shell's PATH) rather than relying on a bare `docker` resolving - the
established, reliable pattern:

```bash
ssh automower-host 'bash -lc "'"$DOCKER"' exec -it debian-dev1 bash"'
```

## `/repos` vs `/share/Repos` - don't conflate host and container paths

`/repos/Automower` only exists **inside** the containers (it's their bind
mount target). On the QNAP host's own shell, the real path is
`/share/Repos/Automower`. Chaining commands with `&&` across a
`docker exec` boundary silently runs the second half in the *outer* shell:

```bash
# WRONG - the `ls /repos/` after && runs on the HOST, where /repos doesn't exist
ssh automower-host "$DOCKER exec debian-dev1 rm -rf /repos/Automower-dev && ls /repos/"
# looks like "/repos/ is gone!" but it's just the wrong shell - the container's
# /repos was never touched. Check with two separate, unambiguous commands instead:
ssh automower-host "$DOCKER exec debian-dev1 ls /repos/"      # container's view
ssh automower-host "ls -la /share/Repos/"                      # host's view
```

## Nested-quoting fragility - the reliable workaround

Anything beyond a trivial one-liner run through
`ssh host -> bash -lc -> docker exec -> bash -c` mangles quotes fast (each
layer re-interprets escaping). Don't fight it with more backslashes -
write the target script to a local scratchpad file and pipe it in over
stdin instead:

```bash
# 1. Write the real script locally (any editor/tool), e.g. to the scratchpad dir
# 2. Pipe it through both hops at once, no inline quoting of the script body needed:
cat script.sh | ssh automower-host 'bash -lc "'"$DOCKER"' exec -i debian-dev1 bash"'
```

This is the pattern to reach for the moment a remote command has its own
quotes, `$variables` that shouldn't expand locally, or multiple statements
- don't spend time hand-escaping, switch to this instead.

## git ownership inside the container

The bind-mounted repo is owned by the host's uid, not the container's
root - a fresh container needs one `git config` before any git command
works:

```bash
git config --global --add safe.directory /repos/Automower
```

(`bootstrap.sh` doesn't do this automatically - it's a one-time
per-container setup step, already done on the current containers.)

## Redeploying after a merge to `main`

Tracking daemons (`debian-dev1`):
```bash
ssh automower "cd /repos/Automower && git pull && ./stopall.sh && ./startall.sh"
```

Web dashboard (`AutomowerWeb` container - note the *different* container
name, same shared working tree so the `git pull` is actually redundant if
already done above, but harmless):
```bash
ssh automower-host 'bash -lc "'"$DOCKER"' exec -w /repos/Automower AutomowerWeb bash -c \"./stopweb.sh && git pull && ./startweb.sh\""'
```
If that nested quoting fights back, use the scratchpad-file pattern above
instead of iterating on escaping.

## Sanity checks after any redeploy

```bash
ssh automower-host "$DOCKER ps"                                  # all 3 containers up?
ssh automower "tmux ls"                                          # all 3 track sessions alive?
ssh automower-host "curl -sI http://localhost:5152/app.css"      # web serving real content (Content-Length > 0, not 0)
```

A `Content-Length: 0` on `app.css` means the ASP.NET Core static-asset
pipeline broke again (see `SKILL.md`'s `MapStaticAssets()` note) - not a
QNAP-level problem, a redeploy/publish problem.

## What's out of scope for this file

First-time container creation, timezone provisioning, the Caddy/TLS/DDNS
setup, and the IPv6 security fix are all one-time setup, already done and
narratively documented in `docs/qnap_infrastructure_setup.md` and
`docs/deployment.md` - reach for those only if setting up a *new* container
or diagnosing something at that layer, not for routine operation.
