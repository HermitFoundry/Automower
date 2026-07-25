# QNAP infrastructure setup notes

Operational knowledge about the QNAP TS-673A NAS (Container Station) this
account's Debian container runs on — host-level facts, not application code,
so they live here rather than in `README.md`/`SKILL.md`. Nothing in this
file is version-controlled by QNAP itself; treat it as tribal knowledge that
needs to be reapplied by hand if the underlying settings ever get reset.

## Reaching the container

Two separate SSH hops, easy to conflate:

- **QNAP host itself**: `ssh terje@192.168.10.142` — lands in the NAS's own
  OS, not the container. `docker` isn't on `PATH` here by default for a
  plain login shell in a non-interactive invocation (see below).
- **Inside the container**: from the host shell,
  `docker exec -it -w /repos/Automower <container-id> bash`. Combine into
  one hop with `ssh terje@192.168.10.142 -t "docker exec -it -w
  /repos/Automower <container-id> bash"`, or an SSH config `Host` alias with
  `RemoteCommand` set the same way.

The `docker` CLI binary lives at
`/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker` on the host -
it's on `PATH` for an *interactive login* shell (via `.profile`/`.bashrc`),
but **not** for a non-interactive one, so `ssh host -t "docker ..."` needs
the full path spelled out explicitly - it won't resolve `docker` from a bare
command string the way an interactive shell would.

The container's ID as of this work: `a87b71026a68` (also its hostname, per
`root@a87b71026a68:...` in every container shell prompt). This is stable
across stop/start, but **changes if the container is ever recreated** - any
saved SSH alias hardcoding it will need updating if that happens.

## Timezone

Container defaulted to UTC (no `/etc/timezone`, `date` showed `UTC`), but
the mowers' own calendar schedules are defined in Europe/Oslo local time by
the Husqvarna app/mower itself - independent of whatever clock the polling
container runs. Every timestamp `AutomowerConsole` produces trusts the
container's system-local clock, so this one setting keeps everything
correct with zero code changes. Fixed (and now part of `bootstrap.sh`, so a
fresh container gets it automatically):

```
apt-get update && apt-get install -y tzdata
ln -fs /usr/share/zoneinfo/Europe/Oslo /etc/localtime
dpkg-reconfigure -f noninteractive tzdata
```

## Container Station networking - what "Default web URL port" actually does

Edit Container → Network → "Default web URL port" toggle looks like a port
publish but **isn't one** - its own tooltip says it's purely a Container
Station dashboard shortcut ("Container Station will use the specified port
... to access this container using the shortcut web URL link"). Confirmed
by `docker port <container-id>` on the host printing nothing after
enabling it - no real Docker-level port mapping gets created. It also can't
be added live to an already-running container regardless (Docker publishes
are fixed at container creation), so this toggle likely only ever takes
real effect on a fresh container create, not an edit-and-apply on a live one.

The container's own internal IP on Container Station's LXD-backed bridge
network (`lxcbr0`, gateway `10.0.3.1`) is `10.0.3.2` (visible in Edit
Container → Network → Connected Networks). This **will change if the
container is recreated** the same as the container ID above.

**Getting real "any device on my LAN can browse to the QNAP's IP" access
still needs solving** - almost certainly means recreating the container
with an explicit port publish (or attaching a bridged/macvlan network
instead of NAT, which gives the container its own real LAN IP directly).
Not done yet; deferred since it would interrupt the 3 live `track` tmux
sessions running in the current container. Do this deliberately, not as a
drive-by, when there's a good window to briefly restart the container.

## Local-network testing without solving the above: SSH tunnel to the container's internal IP

Works today, right now, with zero Container Station changes - tunnel
straight from a Windows machine to the container's internal bridge IP, via
the QNAP host as the relay:

```
ssh -L <local-port>:10.0.3.2:<container-port> terje@192.168.10.142
```

e.g. for `AutomowerWeb` bound to `0.0.0.0:5152` inside the container:

```
ssh -L 15152:10.0.3.2:5152 terje@192.168.10.142
```

then browse to `http://127.0.0.1:15152` (use the literal `127.0.0.1`, not
`localhost` - Windows OpenSSH's `-L` sometimes only binds the IPv4 loopback,
while a browser's `localhost` can resolve to `::1` first and find nothing
listening there).

Pick a *different* local port than whatever the app's own default is if
you're also running a copy of the app directly on the Windows machine
itself (e.g. `AutomowerWeb` also defaults to `5152` locally) - two
processes can't both bind the same local port, and the failure mode
(`bind: Permission denied` / `cannot listen to port`) doesn't obviously say
"already in use."

**If you see the local port accept connections that then immediately
reset/close with no new SSH debug output**: check for a stale/duplicate
`ssh.exe` still holding that same local port from an earlier attempt
(`netstat -ano | grep <port> | grep LISTENING` on Windows) - two tunnel
processes bound to the same port makes the OS route new connections
unpredictably to whichever one, including a dead/broken one. Kill all of
them (`taskkill /F /PID <pid>` for each) and start exactly one fresh tunnel.

### "administratively prohibited: open failed"

This is sshd on the QNAP **refusing** the forwarding channel, not a
Windows/client problem - `AllowTcpForwarding` is disabled server-side.

**The config file that actually matters is not the one you'd expect.**
`ps -ef | grep "/usr/sbin/sshd -f"` on the QNAP host shows the real path:
`/usr/sbin/sshd -f /etc/config/ssh/sshd_config -p 22` - QNAP's persistent
config store, **not** the standard `/etc/ssh/sshd_config` (editing that one
does nothing; confirmed the master `sshd` process ignores it entirely).

```
grep -i AllowTcpForwarding /etc/config/ssh/sshd_config
sudo sed -i 's/^#\?AllowTcpForwarding.*/AllowTcpForwarding yes/' /etc/config/ssh/sshd_config
```

**Do not apply it by toggling SSH off/on in the QNAP GUI** (Control Panel →
Network Access → Telnet/SSH in this QTS version - it's moved around across
QTS versions, not under "Network & File Services" here). That toggle
**regenerates `sshd_config` from QNAP's own internal settings**, silently
reverting the manual edit back to `no` - confirmed by this happening twice
in a row. Instead, reload the running daemon directly, bypassing QNAP's own
service-control script entirely:

```
ps -ef | grep "/usr/sbin/sshd -f"   # find the master sshd's PID (not one of the per-session "sshd: user" lines)
sudo kill -HUP <that PID>
```

`kill -HUP` makes sshd re-read its config file in place without dropping
existing connections and without going through QNAP's regeneration logic.

**This fix lives outside git and isn't persistent** - a NAS reboot, a
firmware update, or touching SSH settings in the GUI again can silently
reset `/etc/config/ssh/sshd_config` back to `AllowTcpForwarding no`, and the
symptom will be the exact same "administratively prohibited" error with no
obvious cause. If SSH tunneling to this NAS mysteriously stops working
again, re-check this file first before assuming something else broke.
