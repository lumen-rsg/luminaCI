FROM fedora:latest

# Install RPM build tools + .NET SDK + NativeAOT dependencies
RUN dnf install -y \
    rpm-build \
    rpmdevtools \
    curl \
    git-core \
    sudo \
    clang \
    gcc \
    make \
    findutils \
    icu \
    libicu-devel \
    && dnf clean all

# Install .NET SDK 10.0 (or latest available)
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet

# Verify installations
RUN dotnet --version && clang --version | head -1

# Create rpmbuild tree
RUN rpmdev-setuptree

WORKDIR /root/rpmbuild

COPY scripts/build-rpm.sh /usr/local/bin/build-rpm.sh
RUN chmod +x /usr/local/bin/build-rpm.sh

# Ensure artifacts dir is writable
RUN mkdir -p /artifacts && chmod 777 /artifacts

ENTRYPOINT ["/usr/local/bin/build-rpm.sh"]