FROM fedora:latest

# Install RPM build tools + multi-protocol source fetching tools.
# NOTE: no `sudo` — the build runs as the unprivileged `rpmbuilder` user, so
# there is nothing to escalate to. (Previously `sudo dnf builddep`/`sudo cp`
# was used in the entrypoint; that served no purpose as the container was
# already root and only widened the attack surface.)
RUN dnf install -y \
    rpm-build \
    rpmdevtools \
    curl \
    wget \
    git-core \
    rsync \
    subversion \
    mercurial \
    tar \
    gzip \
    bzip2 \
    xz \
    && dnf clean all

# Create a dedicated, fixed-id unprivileged user for the build. uid/gid 1000 is
# chosen so it is stable across image rebuilds (matches /opt/lumina/* host bind
# ownership expectations) and so any user-namespace remap on the daemon maps it
# to a non-privileged host uid.
RUN groupadd -g 1000 rpmbuilder \
    && useradd -u 1000 -g 1000 -m -d /home/rpmbuilder -s /bin/bash rpmbuilder

USER rpmbuilder

# Create the rpmbuild tree as the non-root user.
RUN rpmdev-setuptree

WORKDIR /home/rpmbuilder/rpmbuild

# Scripts and entrypoint are owned by root but world-readable/executable; the
# rpmbuilder user invokes them. /artifacts is the bind-mounted output dir — it
# must be writable by uid 1000 (host dir /opt/lumina/builds/<jobId> is created
# and chown'd by build-service).
COPY --chown=rpmbuilder:rpmbuilder scripts/build-rpm.sh /usr/local/bin/build-rpm.sh
RUN chmod +x /usr/local/bin/build-rpm.sh

# Ensure artifacts dir is writable by the build user. (We can't chown a bind
# mount here; build-service sets ownership on the host side. This entry only
# governs the in-image placeholder.)
USER root
RUN mkdir -p /artifacts && chown -R rpmbuilder:rpmbuilder /artifacts
USER rpmbuilder

ENTRYPOINT ["/usr/local/bin/build-rpm.sh"]
