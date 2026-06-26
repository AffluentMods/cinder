Name:           cinder
Version:        0.2.1
Release:        1%{?dist}
Summary:        Open-source digital-forensics toolkit

License:        ASL 2.0
URL:            https://github.com/AffluentMods/cinder
Source0:        https://github.com/AffluentMods/cinder/releases/download/v%{version}/cinder-linux-x64.tar.gz

BuildArch:      x86_64
Requires:       openssl-libs
Requires:       libicu
Recommends:     fuse3
Recommends:     libpff

%description
Cinder consolidates eight separate forensic tools (Autopsy, FTK Imager,
Eric Zimmerman's suite, Volatility, Hindsight, ExifTool, Plaso, WinHex)
into one modern cross-platform application built on .NET 10 and Avalonia 11.
Reads E01 / VHD / VHDX / raw images in-process, parses every common
Windows + Linux artifact, ships court-ready PDF + DOCX reports, and
includes a bring-your-own-model AI copilot.

%prep
%setup -q -c

%build
# Self-contained binary — nothing to build.

%install
install -d %{buildroot}/opt/cinder
install -d %{buildroot}/usr/bin
install -d %{buildroot}/usr/share/applications
install -d %{buildroot}/usr/share/icons/hicolor/512x512/apps
install -d %{buildroot}/usr/share/icons/hicolor/scalable/apps

cp -r ./* %{buildroot}/opt/cinder/
chmod +x %{buildroot}/opt/cinder/Cinder
ln -s /opt/cinder/Cinder %{buildroot}/usr/bin/cinder

install -m 644 ../packaging/linux/cinder.desktop %{buildroot}/usr/share/applications/cinder.desktop
install -m 644 ../assets/branding/png/cinder-512.png %{buildroot}/usr/share/icons/hicolor/512x512/apps/cinder.png
install -m 644 ../assets/branding/cinder-logo.svg %{buildroot}/usr/share/icons/hicolor/scalable/apps/cinder.svg

%post
/usr/bin/gtk-update-icon-cache -q /usr/share/icons/hicolor &>/dev/null || :
/usr/bin/update-desktop-database -q /usr/share/applications &>/dev/null || :
/usr/sbin/setcap cap_sys_rawio,cap_sys_admin+ep /opt/cinder/Cinder &>/dev/null || :

%files
%license LICENSE
/opt/cinder
/usr/bin/cinder
/usr/share/applications/cinder.desktop
/usr/share/icons/hicolor/512x512/apps/cinder.png
/usr/share/icons/hicolor/scalable/apps/cinder.svg

%changelog
* Wed Jun 25 2026 Affluent Labs <hello@cinder.dev> - 0.2.1-1
- Initial RPM packaging
