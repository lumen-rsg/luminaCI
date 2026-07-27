%global debug_package %{nil}

Name:           test_package
Version:        1.0
Release:        1%{?dist}
Summary:        Lumina CI production acceptance canary

License:        GPL-2.0-or-later
URL:            https://github.com/rpm-software-management/rpm
Source0:        hello-1.0.tar.gz
BuildArch:      noarch

%description
Small package used to verify the complete Lumina CI source, build, scan, sign,
and publish pipeline.

%prep
%setup -n hello-1.0

%build

%install
install -m 0755 -d %{buildroot}%{_bindir}
cat > %{buildroot}%{_bindir}/lumina-production-canary << 'EOF'
#!/bin/sh
echo "Lumina CI production canary"
EOF
chmod 0755 %{buildroot}%{_bindir}/lumina-production-canary

%files
%{_bindir}/lumina-production-canary
%doc README

%changelog
* Mon Jul 27 2026 Lumina CI <noreply@lumina.invalid> - 1.0-1
- Production acceptance canary
