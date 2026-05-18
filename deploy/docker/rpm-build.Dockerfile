FROM fedora:latest

# Install RPM build tools + multi-protocol source fetching tools
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
