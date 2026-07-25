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
  `docker exec -it -w /repos/Automower debian-dev1 bash` (container *name*,
  not the hex ID - either works with `docker exec`, but the name is the
  human-readable one and is what's actually used in the SSH alias below).
  Combine into one hop with `ssh terje@192.168.10.142 -t "docker exec -it -w
  /repos/Automower debian-dev1 bash"`, or an SSH config `Host` alias with
  `RemoteCommand` set the same way.

The `docker` CLI binary lives at
`/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker` on the host -
it's on `PATH` for an *interactive login* shell (via `.profile`/`.bashrc`),
but **not** for a non-interactive one, so `ssh host -t "docker ..."` needs
the full path spelled out explicitly - it won't resolve `docker` from a bare
command string the way an interactive shell would. **This bit twice**: the
same PATH gap applies to an SSH config `RemoteCommand` too (it runs the same
way `-t "command"` does) - a `ssh automower` alias set up with a bare
`docker exec ...` failed with `docker: command not found` the first time,
fixed by using the full path in `RemoteCommand` as well, not just in
one-off `-t` invocations.

**Container name: `debian-dev1`.** The hex ID as of this work,
`a87b71026a68` (also its hostname, per `root@a87b71026a68:...` in every
container shell prompt), still works interchangeably with `docker exec`
etc., but the name is what's actually used going forward (docs, the SSH
alias) since it's human-readable and doesn't require looking anything up.
Both the name and the ID are stable across stop/start, but **both change if
the container is ever recreated** - any saved SSH alias will need updating
if that happens.

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

## Public deployment: the two new containers

See `README.md`'s "Public deployment" section for the overall architecture
(Caddy is the only container with published 80/443, reverse-proxying to
AutomowerWeb's own container over the QNAP's own LAN IP rather than
unverified inter-container Docker networking) and `Caddyfile` for the proxy
config itself. From the QNAP host shell, using the full `docker` path (see
"Reaching the container" above for why a bare `docker` fails here):

**`AutomowerWeb` container** - same base setup as the existing `track`
container (Debian, `bootstrap.sh`-provisioned), same `/repos/Automower`
bind mount so it sees the same `.config`/`.data`/git checkout, but with a
**real** Docker port publish this time - Container Station's after-the-fact
"Default web URL port" edit is confirmed not to create one (see "Container
Station networking" above), so create it with an explicit `-p` up front
instead of fighting that UI again:

```bash
DOCKER=/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker
$DOCKER run -d --name automowerweb \
    -p 5152:5152 \
    -v /share/Repos/Automower:/repos/Automower \
    debian:latest \
    sleep infinity
# then: docker exec into it, run ./bootstrap.sh, git clone/checkout, ./startweb.sh
```

(exact base image/provisioning command to be confirmed against whatever the
existing `track` container was actually created with - not verified against
this account's real setup yet, since the container hasn't been created.)

**`Caddy` container** - the only one with 80/443 published, needs the
`Caddyfile` mounted in and a persistent volume for its Let's Encrypt state:

```bash
$DOCKER run -d --name automower-caddy \
    -p 80:80 -p 443:443 \
    -v /share/Repos/Automower/Caddyfile:/etc/caddy/Caddyfile:ro \
    -v caddy-data:/data \
    -e AUTOMOWER_HOSTNAME=<the chosen myQNAPcloud/hermit.no hostname> \
    -e AUTOMOWER_UPSTREAM=192.168.10.142:5152 \
    caddy:latest
```

**Update, 2026-07-25: `AutomowerWeb` container created and verified working.**
Actual commands used and everything hit along the way, below. `Caddy` is
still not created yet.

### Actual `AutomowerWeb` container creation

```bash
DOCKER=/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker
$DOCKER run -d --name AutomowerWeb \
    -p 5152:5152 \
    -v /share/Repos:/repos \
    debian:13 \
    sleep infinity
```

Note the bind mount: `/share/Repos` (the *parent* of the repo, matching
`debian-dev1`'s own mount exactly - confirmed via `docker inspect
<name> --format='{{range .Mounts}}{{.Source}} -> {{.Destination}}{{end}}'`
on both), not the repo directory itself. This means **`debian-dev1` and
`AutomowerWeb` share one single git working tree** - not two independent
clones, whatever "two containers" suggested. Checking out a different
branch inside one container immediately changes what the other sees on
disk. This is fine for source files (a `git checkout` doesn't touch
already-running processes - `debian-dev1`'s 3 live `track` tmux sessions
kept running unaffected when `AutomowerWeb` switched the shared tree from
`feature/blazor-dashboard` to `feature/public-deployment`), but worth
knowing before assuming the two containers are isolated from each other at
the filesystem level - they aren't. They *are* isolated at the process/
port/crash-blast-radius level, which was the actual goal.

Also worth knowing: `.config/config.json`, `.data/*.json`, and the
`track-*.jsonl` logs are all on that same shared tree (gitignored, not
committed) - so `AutomowerWeb` automatically sees the same Husqvarna API
credentials and mower cache `debian-dev1` already has, no separate setup
needed.

### Provisioning: the tar/ICU/apt-signing saga

First attempt reused `bootstrap.sh` as it existed before this date, which
installed the .NET SDK via Microsoft's `dotnet-install.sh` tarball script.
That failed hard on this specific QNAP Container Station setup:

```
tar: ...: Cannot change mode to rwxr-xr-x: Bad address
...
dotnet_install: Error: Extraction failed
```

en masse, for most files in the tarball. Compared `debian-dev1` vs the
freshly-created `AutomowerWeb` container's full `HostConfig` JSON
(`docker inspect <name> --format='{{json .HostConfig}}'`) to rule out a
container-config difference - they were effectively identical, so this
wasn't a container-creation mistake. A `TAR_OPTIONS="--no-same-permissions
--no-same-owner"` workaround got the script to stop erroring, but left a
**broken** install behind (no `/usr/local/bin/dotnet` symlink at all).

The actual clue: `debian-dev1` already had a working `dotnet`, despite
being set up the same way, months earlier - meaning it was never actually
provisioned via this tarball codepath at all, so the bug had simply never
been exercised there. This pointed at apt instead, which sidesteps the
tarball's own `tar` extraction entirely.

Even once reachable, a bare `dotnet --version` on a pure tarball SDK drop
crashed with:

```
Couldn't find a valid ICU package installed on the system
```

- the tarball never installs any system package dependencies, and `libicu`
isn't present on a bare `debian:13` image. An apt-based install pulls
`libicu76` in automatically as a real package dependency, avoiding this
too (a parallel fix also went into `AutomowerWeb.csproj`:
`<InvariantGlobalization>true</InvariantGlobalization>`, safe there
specifically because the app already formats everything via
`CultureInfo.InvariantCulture` explicitly - but that only helps the
*published app*, not the SDK/CLI tooling itself, which is what actually
needed apt).

First apt attempt used the generic/older `packages-microsoft-prod.deb`
config (`.../config/debian/12/...`, a stale copy-paste), which failed
differently:

```
Sub-process /usr/bin/sqv returned an error code (1)
... SHA1 is not considered secure since 2026-02-01
```

Debian 13 "trixie"'s `apt` uses `sqv` (sequoia) for signature verification,
which rejects that key certification's SHA-1 signature under trixie's
stricter default crypto policy. Fixed by using the **Debian-13-specific**
config URL instead - same Microsoft package, a different (non-SHA1-flagged)
signing key certification:

```bash
debian_version="$(. /etc/os-release && echo "$VERSION_ID")"   # "13" on this image
curl -sSL -o /tmp/packages-microsoft-prod.deb "https://packages.microsoft.com/config/debian/${debian_version}/packages-microsoft-prod.deb"
dpkg -i /tmp/packages-microsoft-prod.deb
rm /tmp/packages-microsoft-prod.deb
apt-get update
apt-get install -y dotnet-sdk-10.0
```

This is now what `bootstrap.sh` does (replacing the old tarball section
entirely) - confirmed working end-to-end on `AutomowerWeb`:
`dotnet --version` → `10.0.302`, `libicu76` pulled in automatically, no
errors.

### Passwordless SSH (Windows dev machine → QNAP host)

Set up so commands here can run directly on the NAS instead of relaying
through copy-paste:

```
ssh-keygen -t ed25519 -f ~/.ssh/automower_nas -C "windows-dev-machine"
# public key appended to the QNAP's ~terje/.ssh/authorized_keys
```

`~/.ssh/config` on the Windows machine (two aliases - one that drops
straight into `debian-dev1`'s shell, one that stays at the host level for
`docker` commands):

```
Host automower
    HostName 192.168.10.142
    User terje
    IdentityFile ~/.ssh/automower_nas
    RemoteCommand /share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker exec -it -w /repos/Automower debian-dev1 bash
    RequestTTY yes

Host automower-host
    HostName 192.168.10.142
    User terje
    IdentityFile ~/.ssh/automower_nas
```

Test with `ssh -o BatchMode=yes automower-host true` - `BatchMode=yes`
fails fast (no password prompt hang) if the key isn't accepted yet, instead
of silently blocking on an interactive password prompt that will never be
answered.

### Provisioning + first run, once `bootstrap.sh` was fixed

```bash
DOCKER=/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker
ssh automower-host "$DOCKER exec -w /repos/Automower AutomowerWeb bash -c 'git config --global --add safe.directory /repos/Automower'"
# git refuses to operate in a directory it doesn't own by default - the
# bind-mounted repo is owned by the host's uid, not the container's root.
ssh automower-host "$DOCKER exec -w /repos/Automower AutomowerWeb bash -c 'git fetch origin && git checkout feature/public-deployment && git pull'"
ssh automower-host "$DOCKER exec -w /repos/Automower AutomowerWeb bash -c './bootstrap.sh'"   # must run as root - it is, by default, inside this container
ssh automower-host "$DOCKER exec -w /repos/Automower AutomowerWeb bash -c './startweb.sh'"
```

Verified both from inside the container and from the QNAP host itself
(the latter is the one that actually matters - it's what proves the Docker
port publish is real, not Container Station's broken "Default web URL
port" toggle from earlier in this file):

```
$ curl -sI http://localhost:5152/app.css      # from the QNAP host shell
HTTP/1.1 200 OK
Content-Length: 7410
...
$ docker port AutomowerWeb
5152/tcp -> 0.0.0.0:5152
```

`Content-Length: 7410` (not `0`) on `app.css` confirms the `dotnet publish`
static-asset fix (see `startweb.sh`'s own comments) is working correctly in
Production mode on the real deployment target, not just locally.

### `Caddy` container - created 2026-07-25

Hostname: **`Terje-TS673A.myqnapcloud.com`** (free myQNAPcloud DDNS, QNAP ID
51309252) - this is the NAS's one primary myQNAPcloud device name (Control
Panel → myQNAPcloud → Overview → "primary domain"), not something
per-service - myQNAPcloud gives one DNS name per NAS that just resolves to
its public IP, unrelated to what's actually running behind it. An earlier
guess (`terjes.myqnapcloud.com`) was wrong and never resolved
(confirmed `NXDOMAIN` against 1.1.1.1 and 8.8.8.8, not just a local cache
issue) - the container was recreated once the real name was found in the
Control Panel.

**Port conflict discovered before creating it**: `netstat -tlnp` on the
QNAP host showed `:::443` already `LISTEN`ing - QTS's own admin HTTPS
interface. Binding Caddy straight to host `443` would have failed outright
(`bind: address already in use`). Checked `8880`/`8443` were free and used
those instead - Caddy itself doesn't care what port traffic arrives on
internally, so this only changes what the eventual Altibox forward needs to
target (external `80`→`8880`, external `443`→`8443` on the QNAP's LAN IP),
not anything about how ACME/Let's Encrypt works (that only cares that the
*external*, internet-facing ports are 80/443, which the router forward
still provides).

```bash
DOCKER=/share/CACHEDEV2_DATA/.qpkg/container-station/bin/docker
$DOCKER volume create caddy-data
$DOCKER run -d --name AutomowerCaddy \
    -p 8880:80 -p 8443:443 \
    -v /share/Repos/Automower/Caddyfile:/etc/caddy/Caddyfile:ro \
    -v caddy-data:/data \
    -e AUTOMOWER_HOSTNAME=Terje-TS673A.myqnapcloud.com \
    -e AUTOMOWER_UPSTREAM=192.168.10.142:5152 \
    caddy:latest
```

(the env var is baked in at container creation - fixing the wrong hostname
guess above meant `docker rm -f AutomowerCaddy` + re-running this, not just
editing something live; `caddy-data` is a named volume so the ACME account
state survived the recreate)

**Altibox port-forward rules** (added via the router's own web UI, "Legg
til ny regel" under Portviderekobling) - two separate TCP rules, both
targeting `192.168.10.142`:

| Regel navn         | Ekstern port | Intern port |
|---------------------|:---:|:---:|
| QNAPAutomower       | 80  | 8880 |
| QNAPAutomowerTLS     | 443 | 8443 |

**Fully verified working end-to-end, from outside the LAN**, once both the
correct hostname and the port-forward rules were in place:

```
$ curl -4 -v https://Terje-TS673A.myqnapcloud.com/app.css
< HTTP/1.1 200 OK
< Content-Length: 7410
< Server: Kestrel
< Via: 1.1 Caddy
* Cert: ... issued by Let's Encrypt (E2), CN=terje-ts673a.myqnapcloud.com
```

Confirms the whole chain: Altibox forward → Caddy (`8880`/`8443`) → real
Let's Encrypt cert (auto-obtained, no manual certbot step) → reverse-proxied
to `AutomowerWeb` (`Server: Kestrel`) → the `dotnet publish` static-asset
fix serving real content (`Content-Length: 7410`, not `0`) - all the way
from a real external network, not just LAN-internal `curl`.

**Real security hole found and fixed, 2026-07-25: browsers landed on the
QTS admin login instead of the dashboard.** Root cause: a plain (dual-stack)
`curl https://Terje-TS673A.myqnapcloud.com/` tried the AAAA (IPv6) record
first and got `SEC_E_UNTRUSTED_ROOT` / "self signed certificate" - IPv6
doesn't go through the router's NAT/port-forward rules the way IPv4 does
(those are IPv4-specific NAT concepts; IPv6 hosts are globally routable
directly), and Docker's `-p` port publish doesn't cover IPv6 either by
default. The request was reaching QTS's own admin HTTPS interface directly
instead of Caddy - confirmed by the user's own browser landing on the QTS
login page, not just a curl-only artifact.

There's no dedicated port/protocol firewall app on this NAS/QTS build
(Control Panel → Security only has an IP-based Allow/Deny list, Access
Protection, etc. - no per-port rules, and doesn't distinguish IPv4/IPv6).
Note QTS's own `System → Security → Allow/Deny List` page is *not* the
right tool here even if it looks like it should be.

**Fix: disabled IPv6 entirely on the NAS's own network interface** -
Control Panel → **Network & File Services → Network & Virtual Switch** →
Interfaces → the adapter's IPv6 settings. Nothing on this NAS actually
needs IPv6 (Caddy was never listening on it, nor was anything else meant
to be), so this has no downside. QTS itself flagged this exact scenario
unprompted with a dialog on that page: *"Starting with QTS and QuTS hero
version 5.2.1, IPv6 is disabled by default in Network & Virtual Switch. If
you have already enabled IPv6, it is recommended to disable it to reduce
security risks."* - this NAS had it enabled from before that default
changed, confirming this wasn't a one-off misconfiguration but a known
QNAP-acknowledged risk class.

**Verified fixed**: `nslookup Terje-TS673A.myqnapcloud.com` against 1.1.1.1
now returns only the A record (no AAAA at all - disabling IPv6 on the
interface removed it from myQNAPcloud's DDNS registration too), and a
plain `curl` (no `-4` needed anymore) correctly reaches Caddy → AutomowerWeb
with a valid Let's Encrypt cert and real content.

### Still not done

- QNAP IPv4-side sanity check - confirm `System → Security → Allow/Deny
  List` isn't left more permissive than intended (currently "Allow all
  connections" - fine for now since the only things actually reachable from
  outside are what's deliberately forwarded: Caddy on 8880/8443).
