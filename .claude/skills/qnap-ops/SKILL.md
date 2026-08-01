---
name: qnap-ops
description: This skill should be used when running commands against the QNAP TS-673A NAS or the Debian containers on it that host this project (debian-dev1 for the hybrid-track daemons, AutomowerWeb for the public dashboard, AutomowerCaddy for the reverse proxy) - SSH access, docker exec, deployment/redeploy, or debugging why a remote command's quoting broke. Triggers on "qnap", "the container", "ssh automower", "docker exec", "redeploy", "startall.sh"/"startweb.sh" run remotely.
version: 2.0.0
---

# QNAP operations notes

This repo's actual topology and redeploy commands - the "what to actually
run" cheat sheet for this specific deployment. The general *techniques*
behind each of these (why `docker` isn't on `PATH`, the nested-quoting
workaround, QNAP's own quirks, safe redeploy patterns) are no longer
duplicated here - they live in two global skills available in every
session on this machine: **`qnap-container-station`** (QNAP/Container
Station-specific) and **`docker-ssh-remote-ops`** (generic Docker/SSH).
This file only has what's specific to *this* deployment: real hostnames,
container names, paths, ports, and this repo's own script names.

For narrative/background depth (why ports are what they are, the IPv6
security fix, the provisioning saga) see `docs/qnap_infrastructure_setup.md`
and `docs/deployment.md`.

## Topology

- **QNAP host** (`ssh automower-host`, alias for `terje@192.168.10.142`) -
  the NAS's own OS.
- **`debian-dev1`** - the container running the 3 mowers' `hybrid-track`
  daemons in tmux (`automower-AM405X`, `automower-AM430X`,
  `automower-AM308V` sessions), bind-mounted at `/repos` (host path
  `/share/Repos`), working directory `/repos/Automower`. `ssh automower`
  (alias with `RemoteCommand` set to `docker exec` into this container)
  drops straight into a shell here.
- **`AutomowerWeb`** - separate container running the public dashboard
  (`startweb.sh`), same `/repos` bind mount as `debian-dev1` (they share
  one git working tree, not two independent clones - see
  `docker-ssh-remote-ops`'s "two containers bind-mounting the same host
  path" note for why that's deliberate, not a bug).
- **`AutomowerCaddy`** - reverse proxy/TLS termination, the only container
  with published host ports (`8880`→80, `8443`→443, forwarded from the
  internet by the router). Rarely needs touching.

Any dev/isolated container from earlier feature work (e.g. an
`Automower-dev` checkout) has been retired post-cutover (2026-07-30) - don't
assume one still exists without checking `docker ps -a` first.

## Access

```
ssh automower-host                          # NAS OS itself
ssh automower-host -t "docker exec -it -w /repos/Automower debian-dev1 bash"
ssh automower                                # alias for the line above
DOCKER=/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker   # full path - not on PATH non-interactively, see qnap-container-station
```

Both SSH aliases live in the **local machine's own** `~/.ssh/config`
(Windows: `C:\Users\<you>\.ssh\config`), not on the NAS. See
`docs/qnap-access.md` for the full alias block if it needs recreating.
`/repos/Automower` only exists **inside** the containers - the QNAP host's
own shell sees it at `/share/Repos/Automower` (see `qnap-container-station`
for why chaining `&&` across a `docker exec` boundary can silently run
half a command on the wrong side of that split).

For anything beyond a trivial one-liner through the
`ssh -> bash -lc -> docker exec -> bash -c` chain, use the scratchpad-file
workaround from `docker-ssh-remote-ops` rather than hand-escaping:

```bash
cat script.sh | ssh automower-host 'bash -lc "'"$DOCKER"' exec -i debian-dev1 bash"'
```

`git config --global --add safe.directory /repos/Automower` is already
done on both containers (needed once per fresh container - see
`docker-ssh-remote-ops` for why).

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

## Sanity checks after any redeploy

```bash
ssh automower-host "$DOCKER ps"                                  # all 3 containers up?
ssh automower "tmux ls"                                          # all 3 track sessions alive?
ssh automower-host "curl -sI http://localhost:5152/app.css"      # web serving real content (Content-Length > 0, not 0)
```

A `Content-Length: 0` on `app.css` means the ASP.NET Core static-asset
pipeline broke again (see `.claude/skills/automower-api/SKILL.md`'s
`MapStaticAssets()` note) - not a QNAP-level problem, a redeploy/publish
problem.

## What's out of scope for this file

First-time container creation, timezone provisioning, the Caddy/TLS/DDNS
setup, and the IPv6 security fix are all one-time setup, already done and
narratively documented in `docs/qnap_infrastructure_setup.md` and
`docs/deployment.md` - reach for those only if setting up a *new* container
or diagnosing something at that layer, not for routine operation. For the
generalized version of those same incidents (useful if a *new* project hits
something similar), see the global `qnap-container-station` skill's
`resources/troubleshooting-narratives.md`.
