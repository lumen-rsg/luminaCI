FROM fedora:latest

# Install minimal RPM build tools
RUN dnf install -y \
    rpm-build \
    rpmdevtools \
    curl \
    git-core \
    sudo \
    && dnf clean all

# Create rpmbuild tree as root
RUN rpmdev-setuptree

WORKDIR /root/rpmbuild

COPY scripts/build-rpm.sh /usr/local/bin/build-rpm.sh
RUN chmod +x /usr/local/bin/build-rpm.sh

# Ensure artifacts dir is writable
RUN mkdir -p /artifacts && chmod 777 /artifacts

ENTRYPOINT ["/usr/local/bin/build-rpm.sh"]
