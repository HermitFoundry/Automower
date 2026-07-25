#!/usr/bin/env bash
# One-time provisioning for a fresh Linux container/host that's going to
# build and run AutomowerConsole - see README's "Prerequisites" section.
# Exists because this account's real container (QNAP Container Station,
# Debian base image) had all of this installed by hand over the course of
# a long working session, with nothing recorded anywhere - if that
# container is ever recreated (Container Station update, NAS migration,
# disk failure), none of it survives (a container's writable layer, where
# apt-installed packages and /etc/localtime live, is wiped on remove+
# recreate, though it does survive plain stop/start/reboot). This script
# is that missing record, made re-runnable so it also works as a "did I
# already do this" check on a partially-set-up box.
#
# Every step is idempotent - safe to run again after installing something
# new by hand, or against a container that's only partially provisioned.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root (or via sudo) - installs system packages and writes /etc/localtime." >&2
    exit 1
fi

echo "== apt packages (git, curl, tmux, tzdata, ca-certificates) =="
apt-get update
# ca-certificates: not guaranteed present on a fresh base image (confirmed
# missing on a fresh debian:13 container) - without it, curl can't validate
# any HTTPS connection at all, failing with "error setting certificate
# file: /etc/ssl/certs/ca-certificates.crt" - which breaks the dotnet-
# install.sh fetch below (an HTTPS URL) even though the plain-HTTP apt
# packages above install fine without it.
apt-get install -y --no-install-recommends git curl tmux tzdata ca-certificates

echo "== timezone =="
# Europe/Oslo, not the container's default (often UTC) - the mowers'
# calendar schedules are defined in Norway local time by the Husqvarna app,
# independent of whatever clock the polling host runs. Every timestamp
# AutomowerConsole produces or displays trusts the system-local clock (see
# SKILL.md's "Gotchas" - DateTimeOffset.Now / ToLocalTime() throughout),
# so this one setting keeps all of them correct without any code changes.
ln -fs /usr/share/zoneinfo/Europe/Oslo /etc/localtime
dpkg-reconfigure -f noninteractive tzdata

echo "== .NET SDK (net10.0, required by AutomowerConsole.csproj) =="
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^10\.'; then
    echo "  dotnet SDK 10.x already installed, skipping"
else
    # apt-based install (Microsoft's own package feed), not the
    # dotnet-install.sh tarball this used to run - that tarball's own
    # internal 'tar' extraction hit a real, reproducible "Cannot change
    # mode ... Bad address" failure on this QNAP Container Station setup (a
    # storage/filesystem-level quirk, not a bootstrap.sh bug - confirmed by
    # the exact same command succeeding on a plain apt package instead).
    # Worse, even worked around, the tarball drops SDK files with no system
    # dependencies installed - missing libicu crashed 'dotnet' outright
    # ("Couldn't find a valid ICU package"). apt pulls that in automatically
    # as a real package dependency and never goes through the tar codepath
    # at all - confirmed working end-to-end on this same QNAP setup,
    # 2026-07-25 (Microsoft does publish a Debian 13-specific config with a
    # working signing key - the generic/older debian/12 one hit its own
    # unrelated failure, a SHA-1 signature rejected by trixie's stricter
    # default crypto policy).
    debian_version="$(. /etc/os-release && echo "$VERSION_ID")"
    curl -sSL -o /tmp/packages-microsoft-prod.deb "https://packages.microsoft.com/config/debian/${debian_version}/packages-microsoft-prod.deb"
    dpkg -i /tmp/packages-microsoft-prod.deb
    rm /tmp/packages-microsoft-prod.deb
    apt-get update
    apt-get install -y dotnet-sdk-10.0
fi

echo "== PATH wrappers (am, startall, stopall, startweb, stopweb, startweb.dev, stopweb.dev) =="
# Thin delegating wrappers, not symlinks: the underlying scripts find their
# own repo root via 'dirname "${BASH_SOURCE[0]}"', which a symlink on PATH
# would break (it'd resolve to the symlink's own directory instead of the
# repo). A separate wrapper file that execs the real script by its resolved
# absolute path sidesteps that entirely - and bakes in wherever this repo
# actually lives ($dir, resolved above from bootstrap.sh's own location),
# rather than hardcoding /repos/Automower.
for name in am.sh startall.sh stopall.sh startweb.sh stopweb.sh startweb.dev stopweb.dev; do
    # Wrapper's own PATH name drops a trailing ".sh" (am.sh -> am) but
    # leaves startweb.dev/stopweb.dev exactly as-is - they have no ".sh" to
    # drop in the first place.
    wrapper_name="${name%.sh}"
    printf '#!/usr/bin/env bash\nexec "%s/%s" "$@"\n' "$dir" "$name" > /usr/local/bin/"$wrapper_name"
    chmod +x /usr/local/bin/"$wrapper_name"
    echo "  installed /usr/local/bin/$wrapper_name -> $dir/$name"
done

echo "Done. Verify with: dotnet --version && tmux -V && git --version && curl --version && date && am help"
