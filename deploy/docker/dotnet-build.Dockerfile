# Keep the immutable base and release-only repository policy in sync with the
# generic RPM runner.
FROM fedora:44@sha256:6c75d5bf57cb0fa5aa4b92c6a83c86c791644496d9ac230de7711f5b8ec3b898

LABEL org.opencontainers.image.version="fedora-44-dotnet-v1" \
      io.lumina.build.distribution="fedora" \
      io.lumina.build.release="44" \
      io.lumina.build.dotnet-sdk="10.0.104-1.fc44"

# Install RPM build tools + .NET SDK + NativeAOT dependencies.
# No `sudo` (see rpm-build.Dockerfile for rationale).
RUN dnf --disablerepo='*' --enablerepo=fedora \
    --setopt=install_weak_deps=False install -y \
    rpm-build \
    rpmdevtools \
    curl \
    git-core \
    clang \
    gcc \
    make \
    findutils \
    icu \
    libicu-devel \
    dotnet-sdk-10.0-10.0.104-1.fc44 \
    && dnf clean all \
    && rm -rf /var/cache/dnf

# Verify installations
RUN dotnet --version && clang --version | head -1

# Dedicated unprivileged user with the shared service/builder GID, matching
# rpm-build.Dockerfile.
RUN groupadd -g 1654 lumina-build \
    && useradd -u 1000 -g 1654 -m -d /home/rpmbuilder -s /bin/bash rpmbuilder

USER rpmbuilder

# Create the rpmbuild tree as the non-root user.
RUN rpmdev-setuptree

WORKDIR /home/rpmbuilder/rpmbuild

COPY --chown=rpmbuilder:rpmbuilder scripts/build-rpm.sh /usr/local/bin/build-rpm.sh
RUN chmod +x /usr/local/bin/build-rpm.sh

USER root
RUN mkdir -p /artifacts && chown -R rpmbuilder:lumina-build /artifacts

# The entrypoint starts as root for dependency installation, but raw-spec
# parsing and both rpmbuild phases run as uid 1000 / gid 1654.

ENTRYPOINT ["/usr/local/bin/build-rpm.sh"]
