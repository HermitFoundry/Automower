# Connecting to the QNAP container over SSH

The account's long-running `track` sessions (and `AutomowerWeb`) live in
Debian containers on a QNAP NAS. Getting a shell there is two hops, easy to
conflate:

1. **The QNAP host itself** — plain SSH, lands in the NAS's own OS, not the
   container:

   ```
   ssh <user>@<qnap-ip>
   ```

2. **Into the container, at the repo directory** — from that host shell:

   ```
   docker exec -it -w /repos/Automower <container-name-or-id> bash
   ```

   Combine both into one command from your own machine:

   ```
   ssh <user>@<qnap-ip> -t "docker exec -it -w /repos/Automower <container-name-or-id> bash"
   ```

   `docker` isn't on `PATH` for a non-interactive shell like that `-t`
   invocation (only for an interactive login shell) — if you get
   `docker: command not found`, use the full path instead of a bare
   `docker`, e.g. `/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker`
   (varies by QNAP volume label - `which docker` from an interactive host
   login finds it).

   Worth saving as an SSH config alias so it's just `ssh automower`. **This
   file lives on your own machine — wherever you run `ssh` *from* — not on
   the QNAP or inside the container**, since it configures your local SSH
   client's behavior, not anything remote:

   ```
   # ~/.ssh/config  (on your own machine, e.g. C:\Users\<you>\.ssh\config on Windows)
   Host automower
       HostName <qnap-ip>
       User <user>
       RemoteCommand /share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker exec -it -w /repos/Automower <container-name-or-id> bash
       RequestTTY yes
   ```

   Full `docker` path again here for the same reason as above — `RemoteCommand`
   runs the same way as `ssh host -t "command"`, so a bare `docker` won't
   resolve.

The container's ID is stable across stop/start, but **changes if the
container is ever recreated** — update any saved alias if that happens.

## Testing `AutomowerWeb` from your own machine via an SSH tunnel

Useful when you want to check something in a browser without exposing any
port on the QNAP/router — e.g. verifying a change works on the container
before it's worth setting up real LAN/internet-facing access, or any time
you don't want to touch the running container's network config just to look
at something. Tunnel straight to the container's internal IP through the
QNAP host as the relay (find the container's IP in Container Station → Edit
Container → Network):

```
ssh -L <local-port>:<container-ip>:5152 <user>@<qnap-ip>
```

e.g., with this account's actual values (container IP from Container Station
→ Edit Container → Network, `15152` as the local port since `5152` was
already taken by a locally-running copy of the app):

```
ssh -L 15152:10.0.3.2:5152 terje@192.168.10.142
```

then browse to `http://127.0.0.1:<local-port>` (use the literal
`127.0.0.1`, not `localhost` — Windows OpenSSH's `-L` sometimes only binds
the IPv4 loopback, while `localhost` can resolve to `::1` first and find
nothing listening). Pick a local port that isn't already in use by a copy
of the app running directly on your own machine.

**The tunnel only exists while that SSH session stays connected.** Closing
the terminal (or it disconnecting for any reason) silently kills the
forward — the browser will just fail to load with no obvious explanation,
since nothing about the failure mentions the tunnel at all. If the
dashboard was working and then suddenly isn't, check whether that terminal
is still open before troubleshooting anything else.

If this fails with `channel N: open failed: administratively prohibited`,
the QNAP's sshd has `AllowTcpForwarding` disabled — see
`docs/qnap_infrastructure_setup.md` for the fix (and why the obvious fix,
editing `/etc/ssh/sshd_config`, doesn't work on this QNAP).

For deeper QNAP/Container Station operational notes (timezone, port
mapping, this SSH forwarding issue) see `docs/qnap_infrastructure_setup.md`.
For API implementation notes (auth flow, endpoint quirks, timestamp units,
external references) see `.claude/skills/automower-api/SKILL.md`.
