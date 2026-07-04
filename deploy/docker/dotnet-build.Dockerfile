# Pinned to a specific Fedora major (not :latest) for reproducible builds.
# See rpm-build.Dockerfile for rationale. Keep in sync with it.
FROM fedora:44

# Install RPM build tools + .NET SDK + NativeAOT dependencies.
# No `sudo` (see rpm-build.Dockerfile for rationale).
RUN dnf install -y \
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
    && dnf clean all

# Install .NET SDK 10.0. --channel 10.0 already pins the major; the install
# script resolves it to the latest 10.0.x SDK at build time.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet

# Verify installations
RUN dotnet --version && clang --version | head -1

# Dedicated unprivileged user (uid/gid 1000), matching rpm-build.Dockerfile.
RUN groupadd -g 1000 rpmbuilder \
    && useradd -u 1000 -g 1000 -m -d /home/rpmbuilder -s /bin/bash rpmbuilder

USER rpmbuilder

# Create the rpmbuild tree as the non-root user.
RUN rpmdev-setuptree

WORKDIR /home/rpmbuilder/rpmbuild

COPY --chown=rpmbuilder:rpmbuilder scripts/build-rpm.sh /usr/local/bin/build-rpm.sh
RUN chmod +x /usr/local/bin/build-rpm.sh

USER root
RUN mkdir -p /artifacts && chown -R rpmbuilder:rpmbuilder /artifacts
USER rpmbuilder

ENTRYPOINT ["/usr/local/bin/build-rpm.sh"]
