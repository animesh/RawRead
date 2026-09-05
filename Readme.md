# RawRead + Thermo RawFileReader browser-WASM probe

This is a browser experiment for the existing `RawRead.cs` project.

The goal is to determine whether the original Thermo Fisher `RawFileReader` .NET assemblies can be executed entirely inside a web browser, with the user selecting a Thermo `.raw` file locally and, eventually, receiving an MGF file without uploading the RAW data to a server.

The original `RawRead.cs` is kept unchanged under `reference/RawRead.cs`. The browser probe does not replace the Thermo reader or implement another RAW parser. It calls the same Thermo API used by `RawRead`:

- `RawFileReaderAdapter.FileFactory(...)`
- `SelectInstrument(Device.MS, 1)`
- `RunHeaderEx`
- `GetScanStatsForScanNumber(...)`
- `GetSegmentedScanFromScanNumber(...)`
- `GetCentroidStream(...)`

The browser-specific part is getting a user-selected file into .NET as a `byte[]` and then attempting to use the same Thermo RAW-opening path.

## Current status

The browser-WASM experiment successfully builds and starts .NET in the browser, and the Thermo 8.0.42 managed assemblies successfully load.

RAW opening fails inside Thermo RawFileReader because the browser-WASM runtime is 32-bit from the application's point of view, while Thermo explicitly requires a 64-bit application.

The actual exception observed in the browser was:

```text
System.ApplicationException: Only 64 bit applications are supported by this project
   at ThermoFisher.CommonCore.RawFileReader.Utilities.Validate64Bit(String message, Boolean allow32)
   at ThermoFisher.CommonCore.RawFileReader.RawFileAccess.Create(String fileName, Boolean preferRandomAccess, IViewCollectionManager manager)
   at ThermoFisher.CommonCore.RawFileReader.RawFileReaderAdapter.FileFactory(String fileName)
   at RawProbe.OpenRaw(Byte[] rawBytes, String fileName)
```

The current compatibility boundary is therefore:

```text
Browser
   |
   +-- .NET browser-WASM runtime                  OK
   |
   +-- ThermoFisher.CommonCore.* assemblies       OK
   |
   +-- RawFileReaderAdapter.FileFactory()          FAIL
          |
          +-- Validate64Bit()                      FAIL
```

This is not yet evidence that Thermo's actual RAW-file access code is incompatible with WebAssembly. `Validate64Bit()` is reached before the subsequent RAW access and memory-mapping code is exercised.

## Probe contents

The generated probe contains:

```text
RawReadBrowserWasm-8.0.37/
├── RawReadBrowserWasm.csproj
├── Program.cs
├── index.html
├── main.js
├── NuGet.config
├── README.md
└── reference/
    └── RawRead.cs
```

`reference/RawRead.cs` is deliberately excluded from compilation. Otherwise the original console application's `Main()` is compiled into the browser project and interferes with the browser entry point.

## Thermo version

The probe was initially named `RawReadBrowserWasm-8.0.37` because the experiment started against Thermo 8.0.37.

During restore, Thermo 8.0.37 was not available from the configured package source, so NuGet resolved the Thermo dependencies to **8.0.42**. The successful browser test therefore used Thermo **8.0.42**.

The project references:

```xml
<PackageReference Include="ThermoFisher.CommonCore.RawFileReader" Version="8.0.42" />
<PackageReference Include="ThermoFisher.CommonCore.Data" Version="8.0.42" />
<PackageReference Include="ThermoFisher.CommonCore.MassPrecisionEstimator" Version="8.0.42" />
<PackageReference Include="MathNet.Numerics" Version="5.0.0" />
```

## Prerequisites

.NET 8 SDK or later and the WebAssembly workloads:

```text
dotnet workload install wasm-tools
dotnet workload install wasm-experimental
```

The application uses the .NET `browser-wasm` target and `[JSExport]` interop.

## Build

From the project directory:

```bat
dotnet clean
dotnet restore
dotnet build -c Release
```

The successful build produced:

```text
RawReadBrowserWasm net8.0 browser-wasm succeeded
```

There was one warning:

```text
warning CA1416: JSExportAttribute only supported on browser
```

This warning is expected for this browser-specific project.

An earlier build also showed that `reference/RawRead.cs` must be excluded. Without the exclusion, the original `RawRead2PeakList.Main()` was considered as an entry point and the original code produced `ILogEntryAccess`/`LogEntry` compile errors. The project therefore contains:

```xml
<Compile Remove="reference\RawRead.cs" />
```

## Browser bundle

The .NET build creates the actual browser deployment bundle under:

```text
bin\Release\net8.0\browser-wasm\AppBundle
```

This directory contains the generated browser application and runtime files, including:

```text
AppBundle\main.js
AppBundle\_framework\dotnet.js
AppBundle\_framework\dotnet.native.wasm
```

and the Thermo and supporting assemblies/WASM files.

## First launch attempt and 404

The first launch attempt served the parent directory:

```text
bin\Release\net8.0\browser-wasm
```

instead of `AppBundle`.

The page itself loaded, but the browser then requested:

```text
/_framework/dotnet.js
```

and received:

```text
GET http://localhost:8000/_framework/dotnet.js
net::ERR_ABORTED 404 (File not found)
```

A directory search showed that the generated file was actually at:

```text
bin\Releaseet8.0\browser-wasm\AppBundle\_framework\dotnet.js
```

The generated browser bundle therefore needs to be the document root.

## Correct local launch

For the experiment, a simple Python static server was used.

From the project directory:

```bat
copy /Y index.html bin\Release\net8.0\browser-wasm\AppBundle\index.html
python -m http.server 8000 -d bin\Release\net8.0\browser-wasm\AppBundle
```

Then open:

```text
http://localhost:8000/
Thermo RAW browser-WASM probe
Select a Thermo .raw. The file is processed locally in the browser.

171010_Ip_Hela_ugi.raw
EXCEPTION
System.ApplicationException: Only 64 bit applications are supported by this project
   at ThermoFisher.CommonCore.RawFileReader.Utilities.Validate64Bit(String message, Boolean allow32)
   at ThermoFisher.CommonCore.RawFileReader.RawFileAccess.Create(String fileName, Boolean preferRandomAccess, IViewCollectionManager manager)
   at ThermoFisher.CommonCore.RawFileReader.RawFileReaderAdapter.FileFactory(String fileName)
   at RawProbe.OpenRaw(Byte[] rawBytes, String fileName)
```

The generated `AppBundle\main.js` should be used. It should not be replaced by a manually copied version.

With `AppBundle` served correctly, the browser successfully loaded:

```text
/main.js
/_framework/dotnet.js
/_framework/blazor.boot.json
```

and the Thermo/supporting files, including:

```text
ThermoFisher.CommonCore.Data.dll
ThermoFisher.CommonCore.MassPrecisionEstimator.dll
ThermoFisher.CommonCore.RawFileReader.dll
System.IO.MemoryMappedFiles.dll
MathNet.Numerics.dll
```

This established that the .NET browser runtime and Thermo managed assemblies can be packaged and loaded in the browser.

## RAW-file test

The page provides a file selector for a Thermo `.raw`.

The selected file is read by browser JavaScript and passed to .NET as a `byte[]`. The probe then attempts to enter the same Thermo RAW-opening path used by `RawRead.cs`.

The test does not reach scan parsing.

It fails at:

```text
RawFileReaderAdapter.FileFactory(fileName)
```

with:

```text
System.ApplicationException: Only 64 bit applications are supported by this project
```

The relevant call chain is:

```text
ThermoFisher.CommonCore.RawFileReader.Utilities.Validate64Bit
    ↓
ThermoFisher.CommonCore.RawFileReader.RawFileAccess.Create
    ↓
ThermoFisher.CommonCore.RawFileReader.RawFileReaderAdapter.FileFactory
    ↓
RawProbe.OpenRaw
```

This is a Thermo exception, not a JavaScript exception.

## Why it fails

The standard .NET browser-WASM target is:

```xml
<RuntimeIdentifier>browser-wasm</RuntimeIdentifier>
```

The application has a 32-bit address model from the application's point of view. Thermo RawFileReader explicitly checks for a 64-bit application before creating its RAW-file access object.

Therefore:

```text
.NET browser-WASM
    |
    | 32-bit address model
    v
Thermo Validate64Bit()
    |
    v
FAIL
```

There is currently no official .NET SDK target that can simply be changed to:

```text
browser-wasm64
```

The problem is therefore not that the browser cannot execute WASM, nor that the Thermo assemblies cannot be loaded. The immediate problem is the mismatch between the current .NET browser-WASM address model and Thermo's 64-bit requirement.

## What has NOT been established

The experiment has not yet established that:

- Thermo RawFileReader cannot work in WebAssembly.
- Thermo's memory-mapped file implementation cannot work in WebAssembly.
- Thermo requires an operating-system native Thermo library at this point.
- Browser file storage/access is fundamentally incompatible with the Thermo reader.
- Browser-side MGF generation is impossible.

Those remain untested because `Validate64Bit()` stops execution first.

## Thermo assembly investigation

The Thermo 8.0.42 assemblies were inspected during the investigation.

The core RawFileReader assembly contains references/strings associated with:

```text
System.IO.MemoryMappedFiles
MemoryMappedFile
MemoryMappedViewAccessor
UnmanagedMemoryAccessor
FileStream
FileAccess
FileMode
MemoryMappedRawFile
MemoryMappedRawFileManager
MemoryMappedFileHelper
RawFileAccessBase
ThreadSafeRawFileAccess
```

This suggests that, after the 64-bit check is dealt with, the memory-mapped-file implementation is an important next compatibility boundary.

This is a future test, not a confirmed incompatibility.

## Why the Thermo DLL is not being patched yet

It would be possible in principle to modify/decompile the Thermo assembly and bypass `Validate64Bit()`.

That would only remove the guard. It would not demonstrate that the code behind the guard works in browser WASM.

The preferred experiment is therefore to obtain a genuine 64-bit-address .NET WASM runtime and run the unmodified Thermo assembly against it.

## WASM64 is a real WebAssembly capability

The issue is not that WebAssembly cannot be 64-bit.

WebAssembly has the `memory64` capability, and Emscripten supports memory64 builds.

The relevant distinction is:

```text
WebAssembly memory64                         available
Emscripten memory64                          available
.NET official browser-wasm target            currently 32-bit
.NET official browser-wasm64 target          not currently available
Thermo RawFileReader                         requires 64-bit
```

The missing component for this experiment is a .NET runtime/toolchain capable of running the browser application with a wasm64 address model.

## Current .NET status

The .NET runtime project has an explicit tracking issue for WebAssembly Memory64 support:

```text
https://github.com/dotnet/runtime/issues/94108
```

The current official browser-WASM workflow still uses:

```text
browser-wasm
```

rather than a supported `browser-wasm64` runtime identifier.

Consequently, this is not solved by simply changing the existing project file to an undocumented RID.

## Potential future directions

### 1. Build a wasm64-capable .NET runtime from source

This is the most direct next experiment.

The .NET runtime can be built from source for WebAssembly, and its native WebAssembly runtime is built using Emscripten.

The investigation should determine whether the Mono WebAssembly runtime can be built using Emscripten's memory64 configuration.

The first proof-of-concept should be deliberately small:

```text
custom .NET WASM runtime
        ↓
wasm64
        ↓
IntPtr.Size == 8
        ↓
load ThermoFisher.CommonCore.RawFileReader.dll
        ↓
RawFileReaderAdapter.FileFactory(...)
```

If this succeeds, repeat the RAW-opening test without modifying Thermo.

### 2. Investigate CoreCLR WebAssembly

The .NET project is also developing CoreCLR support for WebAssembly.

This is a separate runtime/build path from the current Mono browser-WASM application and should be investigated independently.

The relevant question is whether a CoreCLR WebAssembly build can actually be produced with a 64-bit address model and run in a browser, not merely whether CoreCLR can run WebAssembly.

### 3. Test Thermo's post-check code

If a wasm64 .NET runtime succeeds, the next test is the unmodified Thermo implementation:

```text
RawFileAccess.Create()
        ↓
MemoryMappedRawFile
        ↓
MemoryMappedRawFileManager
        ↓
MemoryMappedFileHelper
        ↓
actual RAW access
```

This will tell us whether the 64-bit check was the only blocker or merely the first blocker.

### 4. Revisit the browser file layer

The current probe uses a browser-selected file and passes its bytes into the .NET WASM environment.

That is sufficient for the compatibility experiment.

The eventual application may need a more efficient file-access architecture, especially for large RAW files, because duplicating a large RAW file between browser JavaScript memory and WASM memory could be expensive.

That optimization should be addressed only after Thermo RAW opening works.

## Intended final application

The final target remains:

```text
User selects sample.raw
        |
        v
Browser
        |
        | local only
        v
.NET WASM + Thermo RawFileReader
        |
        v
existing RawRead scan logic
        |
        v
MGF in memory
        |
        v
browser Blob
        |
        v
download sample.mgf
```

The RAW file should never be uploaded to a server.

The scientific/raw-reading logic should remain based on the existing `RawRead.cs`. Browser-specific code should be limited to file selection, interop, and downloading the resulting MGF.

## Current conclusion

The first browser experiment was successful as a **runtime and assembly-loading test**, but unsuccessful as a **RAW-opening test**.

What has been demonstrated:

```text
.NET browser-WASM starts                         YES
Browser JavaScript ↔ .NET interop               YES
Thermo 8.0.42 managed assemblies load           YES
Thermo FileFactory() can be called               YES
Thermo RAW file can currently be opened          NO
Immediate reason                                  64-bit requirement
```

The project is currently blocked by the **64-bit requirement of Thermo RawFileReader**, not by an inability to package .NET or Thermo assemblies into browser WASM.

The next technically meaningful experiment is therefore to investigate/build a **wasm64-capable .NET runtime**, starting with the existing Mono browser-WASM implementation if practical, and then repeat the exact same Thermo `FileFactory()` test.

Only if that succeeds should the project proceed to scan extraction and browser-side MGF generation.
