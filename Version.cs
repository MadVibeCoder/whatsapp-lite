// The one place to bump the version. build.ps1 compiles this file into both the app and the installer,
// and the installer reports it to Windows (Settings > Apps) from here. linux/build-deb.py reads it too.
using System.Reflection;

[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]
[assembly: AssemblyInformationalVersion("1.4.0")]
