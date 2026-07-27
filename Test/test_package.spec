%global debug_package %{nil}

Name:           test_package
Version:        1.0
Release:        1%{?dist}
Summary:        Test package for Lumina CI pipeline verification

License:        MIT
URL:            https://github.com/lumen-rsg/lumina-ci
Source0:        %{name}-%{version}.tar.gz
BuildArch:      noarch

%description
Test package for verifying the full Lumina CI build pipeline including
source fetch, RPM build, CVE scan, and artifact download.

%prep
%setup -n %{name}-%{version}

%build
# No compilation needed for test package

%install
install -m 0755 -d %{buildroot}%{_bindir}
install -m 0755 -d %{buildroot}%{_sysconfdir}/test_package
install -m 0755 -d %{buildroot}%{_mandir}/man1

# Create a simple test script
cat > %{buildroot}%{_bindir}/test_package << 'EOF'
#!/bin/bash
echo "Lumina CI test package - build verified successfully"
echo "Build timestamp: $(date)"
exit 0
EOF
chmod 755 %{buildroot}%{_bindir}/test_package

# Create a config file
cat > %{buildroot}%{_sysconfdir}/test_package/config.ini << 'EOF'
[test]
name=test_package
version=1.0
status=verified
EOF

cat > %{buildroot}%{_mandir}/man1/test_package.1 << 'EOF'
.TH TEST_PACKAGE 1
.SH NAME
test_package \- verify a Lumina CI RPM installation
.SH SYNOPSIS
.B test_package
.SH DESCRIPTION
Prints a confirmation that the Lumina CI lifecycle package is installed.
EOF

%files
%{_bindir}/test_package
%dir %{_sysconfdir}/test_package
%config(noreplace) %{_sysconfdir}/test_package/config.ini
%{_mandir}/man1/test_package.1*
%doc README

%changelog
* Mon May 18 2026 Lumina CI <noreply@lumina.1t.ru> - 1.0-1
- Initial test package for CI pipeline verification
