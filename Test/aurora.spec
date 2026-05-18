%global debug_package %{nil}

Name:           aurora
Version:        2.1
Release:        1%{?dist}
Summary:        The package manager that doesn't waste your time

License:        UNLICENSED
URL:            https://github.com/lumen-rsg/aurora.net
Source0:        %{name}-%{version}.tar.gz

ExclusiveArch:  x86_64 aarch64

%description
Aurora is the package manager and bootstrap engine for Lumina.
Built on the battle-tested foundation of the RPM ecosystem, Aurora replaces
slow Python wrappers with a blazing-fast orchestrator compiled to NativeAOT
in modern C#. It features iterative BFS dependency resolution, parallel
repository downloads, direct SQLite metadata querying, and fuzzy matching
for missing capabilities.

%prep
%setup -n %{name}-%{version}

%build
dotnet publish Aurora.CLI/Aurora.CLI.csproj \
    -c Release \
    -r linux-x64 \
    -o %{_builddir}/publish \
    --self-contained true

%install
install -m 0755 -d %{buildroot}%{_bindir}
install -m 0755 -d %{buildroot}%{_libexecdir}/aurora
install -m 0755 -d %{buildroot}%{_sysconfdir}/aurora

# Install the main binary
install -m 0755 %{_builddir}/publish/au %{buildroot}%{_bindir}/au

# Install runtime dependencies (SQLite native lib, etc.)
for f in %{_builddir}/publish/libe_sqlite3.so %{_builddir}/publish/*.dll; do
    [ -f "$f" ] && install -m 0644 "$f" %{buildroot}%{_libexecdir}/aurora/
done

%files
%{_bindir}/au
%dir %{_libexecdir}/aurora
%{_libexecdir}/aurora/libe_sqlite3.so

%changelog
* Fri Apr 24 2026 Lumen Research Group <noreply@lumina.1t.ru> - 2.1-1
- Initial RPM packaging with NativeAOT build.
