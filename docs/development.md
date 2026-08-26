# Development

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer
- Windows (the app and tests target `net10.0-windows`)

## Build and Run

```sh
git clone https://github.com/loxsmoke/openlogi-net.git
cd openlogi-net

# build the whole solution
dotnet build OpenLogi.slnx

# run the desktop app
dotnet run --project src/OpenLogi.App

# run the CLI
dotnet run --project src/OpenLogi.Cli -- list
```

## Test

```sh
dotnet test OpenLogi.slnx
```

## Build an Installable Release

Releases are produced by the [`Release`](../.github/workflows/release.yml)
GitHub Action (manually triggered): it fetches the next version, stamps it into
the projects, publishes a self-contained, trimmed build, and packages both an
Inno Setup installer and a portable zip. To reproduce the release build locally:

```sh
dotnet publish src/OpenLogi.App/OpenLogi.App.csproj -c Release -r win-x64 \
  --self-contained -p:PublishTrimmed=true -p:TrimMode=partial -o publish
```

The build is self-contained (the .NET runtime is bundled) and trimmed in
partial mode -- only the .NET base libraries are trimmed, keeping the package
around 20 MB while leaving device I/O, config, and UI code untouched.
