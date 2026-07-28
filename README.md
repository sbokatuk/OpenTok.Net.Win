# OpenTok.Net.Win

WinUI 3 video rendering for Vonage's OpenTok Windows SDK — the piece that lets a **WinUI or .NET
MAUI** app on Windows actually show OpenTok video.

```bash
dotnet add package OpenTok.Net.Win
```

## Why this exists

Unlike its two sibling repositories, **this is not a binding**. `OpenTok.Client` is already managed
.NET, so there is no Objective-C or Java surface to project into C# and nothing here is generated.

What is missing from `OpenTok.Client` is a renderer that works in WinUI:

| Asset in `OpenTok.Client` | Renderer it ships |
| --- | --- |
| `net462` | `WPFVideoRenderer`, `WinFormsVideoRenderer` |
| `netcoreapp3.1` | `WPFVideoRendererNetCore`, `WinFormsVideoRendererNetCore` |
| **`netstandard2.0`** — the one a modern .NET app resolves | **none** |

WPF and Windows Forms renderers cannot be used from WinUI 3, and .NET MAUI on Windows *is* WinUI 3.
So without this package a MAUI app can connect, publish and subscribe perfectly well and still have
nowhere to put the picture.

## What it adds

| Type | For |
| --- | --- |
| `OpenTokVideoView` | A WinUI control that displays one stream. Hand `view.Renderer` to a publisher or subscriber. |
| `OpenTokVideoRenderer` | The `IVideoRenderer` itself, if you want to own the `Image` yourself. |
| `OpenTokDispatcher` | An `IDispatcher` that delivers every SDK event on the UI thread. |
| `I420Converter` | The I420 → BGRA8 conversion, public because a custom renderer needs it too. |

```csharp
// Every OpenTok event arrives on the UI thread because of this one argument.
var context = new Context(new OpenTokDispatcher(DispatcherQueue.GetForCurrentThread()));

var view = new OpenTokVideoView();
var publisher = new Publisher.Builder(context) { Renderer = view.Renderer }.Build();

var session = new Session.Builder(context, apiKey, sessionId).Build();
session.Connected += (_, _) => session.Publish(publisher);
session.Connect(token);
```

`OpenTokDispatcher` is the part most easily missed. The default `Context` raises events on the SDK's
own threads, and touching any XAML object from there throws `RPC_E_WRONG_THREAD` — in exactly the
handlers that need to change the UI. It is preferred over the SDK's own `ThreadPoolDispatcher`
because `DispatcherQueue` is FIFO: a `StreamDropped` cannot overtake its `StreamReceived` and leave
an orphaned tile on screen.

## x64 only

`OpenTok.Client`'s native payload — `opentok.dll` and its three capturers — is built for **x64
only**. There is no arm64 build in the package and no separate arm64 package.

The package ships `build/OpenTok.Net.Win.targets`, which fails the build with **OTW0001** if you
target arm64, rather than letting it surface as a `BadImageFormatException` after the app has
started. Windows on ARM runs the x64 build under emulation; set
`OpenTokSkipArchitectureCheck=true` if you are supplying the native payload yourself.

## Versioning

`<native SDK version>.<binding revision>`, the same scheme the sibling repositories use, so
`OpenTok.Net.Win 2.34.1.x` means "sits on Vonage's Video SDK 2.34.1". `OpenTok.Net`'s façade
requires all platform heads to agree on the first three components.

## Building

```bash
dotnet pack src/OpenTok.Net.Win --configuration Release -o artifacts
```

Packing needs Windows and the Windows App SDK. *Compiling* does not:

```bash
./build/CompileCheck.sh
```

runs the C# compiler over every Windows target framework on any operating system, using
`EnableWindowsTargeting=true` to restore the reference packs and stopping at `-t:Compile` — a full
build would go on to run `MakePri.exe`, which really is Windows-only. It catches everything the
compiler can: missing members, wrong signatures, and the interface-constraint mismatches that the
SDK's XML documentation does not record.

The tests are the other exception, and this one is deliberate in the design:

```bash
dotnet test tests/OpenTok.Net.Win.UnitTests
```

runs anywhere. `I420Converter` is written with no WinUI, Windows or OpenTok types in it, and that
test project compiles the same source file into a plain `net9.0` assembly. The conversion is the one
piece here that is pure arithmetic over raw bytes and can be wrong without looking wrong — a bad
chroma stride or a full-range coefficient set produces a picture that is merely slightly off — so it
is worth being able to verify on any machine. CI runs it on Ubuntu for the same reason.

## Layout

| | |
| --- | --- |
| `src/OpenTok.Net.Win` | The package |
| `samples/OpenTok.Sample.WinUI` | Connect, publish, subscribe — against the packed `.nupkg`, not a project reference |
| `tests/OpenTok.Net.Win.UnitTests` | The converter, runnable on any OS |
| `tests/OpenTok.Net.Win.PackageTests` | What the packed `.nupkg` contains |
