## Building

To build

```
cd DumpSymbolicate
msbuild
```

## Symbolicating

```
./symbolicate-crash.sh <path-to-crash-log-folder>
```

This script will download the appropriate versions of Mono and Visual Studio for Mac, unpack and scan them for assemblies and executables and try to symbolicate the crash report. It will create a file called CrashReportSymbolicated.json in the crash log folder.

It will only be able to find the Mono and Visual Studio downloads if the requested version has been released through the updater service
