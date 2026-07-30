# Public deployment

The dashboard is live at `https://Terje-TS673A.myqnapcloud.com/` — see
`docs/qnap_infrastructure_setup.md` for the QNAP-specific provisioning
steps, and `Caddyfile` for the reverse-proxy config:

```
Internet --(Altibox: forward 80->8880, 443->8443 only, not the router's
             "DMZ" feature, which forwards everything unfiltered and would
             expose the QNAP's own admin UI/SSH too)--> QNAP LAN IP:8880/8443
    --> [Caddy container]  (the only container with published host ports)
            reverse_proxy --> <qnap-lan-ip>:5152 --> [AutomowerWeb container]
```

- **`AutomowerWeb` gets its own container**, separate from the one running
  `track` — a public web server's crashes/restarts/attack surface
  shouldn't share a blast radius with the always-on mower tracking. Both
  bind-mount the same `/repos/Automower` host path, so both see the same
  `.config`/`.data` and git checkout.
- **`Caddy`** (official `caddy:latest` image) terminates TLS and gets
  automatic Let's Encrypt certificates — see `Caddyfile`'s own comments for
  the required `AUTOMOWER_HOSTNAME`/`AUTOMOWER_UPSTREAM` env vars and why
  the upstream target is the QNAP's LAN IP, not `localhost` (which inside a
  container means that container, not the host or a sibling container).
  Published on host ports **8880/8443**, not the standard 80/443 — QTS's own
  admin interface already holds 443 on this NAS (confirmed via `netstat` on
  the host before creating the container). Caddy itself doesn't care what
  port traffic arrives on, so the Altibox forward just maps external 80/443
  to these instead — Let's Encrypt's HTTP-01 challenge only needs the
  *external* ports to be 80/443, not the internal ones.
- **Hostname**: `Terje-TS673A.myqnapcloud.com` (free `myQNAPcloud` DDNS —
  the NAS's one primary device hostname, not something per-service) — a
  `hermit.no` subdomain remains an easy upgrade later if wanted.
- **No authentication, deliberately** — the only data exposed is already
  coarse/low-stakes: activity, battery, mower model/serial, and a
  municipality-level place name (`LocationService` reverse-geocodes to
  `zoom=12`, e.g. "Asker, Norway" — not precise enough to locate the
  property), no controls. `Caddyfile` has a one-line `basicauth` upgrade
  path commented in, ready whenever it's wanted, without touching
  `AutomowerWeb` itself.
- **Serving a second site later doesn't need a new container, port, or
  router rule** — Caddy already owns the only forwarded ports (8880/8443)
  and dispatches by hostname/path to as many backends as wanted. The free
  myQNAPcloud name is one hostname per NAS, not per app, but a second site
  can share it via a path (`terje-ts673a.myqnapcloud.com/otherapp/`) or get
  its own hostname (e.g. a `hermit.no` subdomain) pointed at the same
  public IP — either way it's just another block in `Caddyfile`, which gets
  its own automatic Let's Encrypt cert with no extra config. See
  `Caddyfile`'s own comments for both patterns.
