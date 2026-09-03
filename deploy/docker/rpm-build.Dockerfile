# Pin the multi-platform Fedora 44 image by OCI index digest. Bootstrap against
# the signed release and updates repositories so the toolchain remains aligned
# with the package versions already present in the current base image.
FROM fedora:44@sha256:b3c47a22ef314698c276cf84a47aef414fb96d87d9587bd0808c93d1d44cb28b

LABEL org.opencontainers.image.version="fedora-44-v2" \
      io.lumina.build.distribution="fedora" \
      io.lumina.build.release="44"

# Install RPM build tools, a minimal C/C++ build toolchain, and multi-protocol
# source-fetching tools.
#
# Toolchain (FUNC-001): the stock image previously shipped no compiler, so any
# spec whose %build actually compiles (gcc/make/cmake, autotools) failed with
# "command not found". gcc/g++/make/cmake + autotools cover the bulk of real
# specs; .NET specs still need the separate lumina-dotnet-build image.
#
# NOTE: no `sudo` — and none is needed. dnf builddep runs as root in the
# entrypoint (it must, to install into /usr/lib and write /var/lib/rpm); the
# untrusted %build/%install shell then runs as the `rpmbuilder` user. See the
# privilege-split comment at the ENTRYPOINT below and in build-rpm.sh.
RUN dnf --disablerepo='*' --enablerepo=fedora --enablerepo=updates \
    --setopt=install_weak_deps=False install -y \
    rpm-build \
    rpmdevtools \
    gcc \
    gcc-c++ \
    make \
    cmake \
    autoconf \
    automake \
    libtool \
    curl \
    jq \
    wget \
    git-core \
    rsync \
    subversion \
    mercurial \
    tar \
    gzip \
    bzip2 \
    xz \
    && dnf clean all \
    && rm -rf /var/cache/dnf

# UID 1000 is the unprivileged build identity. GID 1654 is shared with the
# service containers' built-in `app` identity, so both sides can write setgid
# artifact/source volumes without making them world-writable.
RUN groupadd -g 1654 lumina-build \
    && useradd -u 1000 -g 1654 -m -d /home/rpmbuilder -s /bin/bash rpmbuilder

# Create the rpmbuild tree as the non-root user (the tree only needs to exist;
# the entrypoint later chowns it so the build phase owns its contents).
USER rpmbuilder
RUN rpmdev-setuptree

WORKDIR /home/rpmbuilder/rpmbuild

# Scripts and entrypoint are owned by root but world-readable/executable.
# /artifacts is the bind-mounted output dir — it must be writable by uid 1000
# (host dir /opt/lumina/builds/<jobId> is created and chown'd by build-service);
# we chown the in-image placeholder here as a fallback for the non-root copy.
COPY --chown=rpmbuilder:lumina-build scripts/build-rpm.sh /usr/local/bin/build-rpm.sh
COPY --chown=rpmbuilder:lumina-build scripts/lumina-kubernetes-build /usr/local/bin/lumina-kubernetes-build
COPY deploy/docker/lumina-fedora.repo /etc/yum.repos.d/lumina-fedora.repo
RUN chmod +x /usr/local/bin/build-rpm.sh /usr/local/bin/lumina-kubernetes-build

USER root
RUN mkdir -p /artifacts && chown -R rpmbuilder:lumina-build /artifacts

# The entrypoint starts as root only so dnf can install dependencies into the
# writable overlay. It drops to rpmbuilder before any raw-spec parsing, creates
# an SRPM, returns to root only to resolve that SRPM's dependency header, then
# drops again for the untrusted rebuild.
#
# The drop uses `setpriv` (direct setuid/setgid syscalls), NOT su/sudo/runuser:
# the build container is launched with the per-container `no-new-privileges`
# security opt (see DockerBuildService.cs), which neutralizes setuid binaries,
# so only a syscall-based drop works.
ENTRYPOINT ["/usr/local/bin/build-rpm.sh"]
