# RawRead

`RawRead.cs` contains the C# code for extraction of all info in raw file. This is an updated version of `RawRead.cs` using the .NET 8 assemblies distributed in the ThermoFisher `RawFileReader` repository. The [repository](https://github.com/thermofisherlsms/RawFileReader) and its [fork](https://github.com/animesh/RawFileReader/), contains the ThermoFisher CommonCore 8.0.42  packages under `Libs/NetCore/Net8/`. The project keeps the original RawRead functionality: RAW metadata, mass-precision output, base-peak chromatogram, centroid/profile MGF output, MS1 filtered peak output, and FFT output.

## Quick start

### Windows

InstaOpen terminal with ctrl+R `cmd.exe` 

```bash
winget install --id Git.Git -e --source winget
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/animesh/RawFileReader
git clone https://github.com/animesh/RawRead
cd RawRead
dotnet build -c Release
dotnet bin/Release/net8.0/RawRead.dll 171010_Ip_Hela_ugi.raw
```

### Linux

```bash
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
bash dotnet-install.sh --channel 8.0 --install-dir "$PWD"
git clone https://github.com/animesh/RawFileReader
git clone https://github.com/animesh/RawRead
cd RawRead
../dotnet build -c Release
../dotnet bin/Release/net8.0/RawRead.dll 171010_Ip_Hela_ugi.raw
```

## Key outputs 

`<raw>.MZ.txt` — mass precision estimates (Mass, mmu, ppm)
`<raw>.chromatogram.txt` — BasePeak chromatogram (RT, intensity)
`<raw>.centroid.MGF`, `<raw>.profile.MGF` — MS2 blocks for centroid/profile scans
`<raw>.profile.intensity{insThr}.charge{chgThr}.MS.txt` — per-scan profile/intensity listing
`<raw>.intensity{insThr}.charge{chgThr}.FFT.txt` — FFT of max-intensity over scan index (see below)
`<raw>.mzFFT.txt` — mass-domain FFT of averaged MS1 spectrum (see below)

### FFT output interpretation

The FFT file columns are `bin`, `frequency_per_min`, `period_min`, `magnitude`.

The signal being transformed is the per-scan maximum centroid intensity, sampled at the acquisition rate. Each FFT bin `k` corresponds to a periodic pattern that repeats every `period_min = 1 / frequency_per_min` minutes across the run. Only the first `N/2` bins are written (the second half is the conjugate mirror of a real-valued input).

**Key bins in a typical DDA run** (example: 10.7-min Orbitrap Elite, 1221 scans):

| bin | period | interpretation |
|-----|--------|----------------|
| 0 | ∞ | DC — mean intensity across all scans |
| 1 | = run length | Full LC gradient envelope |
| 2–5 | half/quarter run | Broad chromatographic cluster envelopes |
| ~11 | ~1 min | DDA TopN precursor cycle repetition period |
| ~31 | ~20 sec | MS2 burst rate within a TopN cycle |
| 72–98 | 6–9 sec | Individual survey + fragmentation scan timing |

High-magnitude bins at short periods (< 0.2 min) indicate the instrument's raw scan repeat rate. A strong bin near 1 minute is characteristic of DDA TopN methods. Peaks in the 2–5 min range reflect the chromatographic peak cluster structure of the gradient.

### Mass-domain FFT (`<raw>.mzFFT.txt`)

Columns: `bin`, `spacing_Da`, `magnitude`.

The averaged MS1 spectrum is placed on a 0.01 Da grid and FFT'd. A magnitude peak at a given `spacing_Da` means ions appear at regular intervals of that many Da across the averaged spectrum. This reveals:

| spacing_Da | interpretation |
|---|---|
| 1.0 | z=1 isotope envelope |
| 0.5 | z=2 isotope envelope (most peptides) |
| 0.33 | z=3 |
| 0.25, 0.2, … | z=4, z=5, … — multiply charged peptides |
| 14 | CH₂ repeat — lipid/fatty acid series |
| 22 | CO₂ loss ladder or specific polymer |
| 44 | CO₂ (44 Da) repeat — polyether/lipid |
| 162 | hexose sugar unit — glycan series |

Strong peaks at sub-Da spacings (0.1–0.5 Da) directly report the dominant charge states in the run. A run dominated by doubly and triply charged tryptic peptides will show the highest magnitude at `spacing_Da ≈ 0.33–0.50`.

## Notable behaviors & caveats

Uses GetReaction(0).PrecursorMass for PEPMASS in MGF; some scans may lack reactions — code assumes they exist.
Extracts charge from trailer labels by matching "Charge State:" — trailer labeling may vary by instrument/firmware.
Heuristic branches (e.g., title.Contains(" ms ")) determine some output formats; these heuristics may not be universal.
Large files: the code keeps arrays sized by the number of scans and may use significant memory for very long runs.

The ThermoFisher packages are version 8.0.42. The project uses MathNet.Numerics 5.0.0 for the FFT, which is compatible with .NET 8.


## Build and Run with system-wide dotnet installation

```bash
dotnet build -c Release
dotnet run -c Release -- /path/to/file.raw
dotnet run -c Release -- /path/to/file.raw 1000
dotnet run -c Release -- /path/to/file.raw 1000 2
```

After building:

```bash
dotnet bin/Release/net8.0/RawRead.dll /path/to/file.raw
```

The RAW reader itself is the ThermoFisher CommonCore implementation. No old Planet Orbitrap / legacy DLL references remain.

## Important migration changes

- Target framework changed from the old Mono/.NET Framework compilation model to `net8.0`.
- ThermoFisher CommonCore packages changed to 8.0.42, matching the uploaded repository.
- The old command-line `mcs`/`csc` reference instructions were replaced with a `.csproj`.
- The scan array is indexed relative to `FirstSpectrum`, rather than assuming the first scan is 1.
- FFT phase uses `Atan2` rather than `Atan(imaginary / real)`.
- Numeric command-line parsing uses invariant culture.
- RAW acquisition/error handling was made explicit.

The actual Thermo API calls used by RawRead remain the same APIs exposed by the 8.0.42 assemblies, including `RawFileReaderAdapter.FileFactory`, `GetSegmentedScanFromScanNumber`, `GetCentroidStream`, chromatogram access, and mass-precision estimation.
