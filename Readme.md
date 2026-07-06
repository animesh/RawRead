## Prerequisites
* [Mono](http://www.mono-project.com/download/stable/#download-lin) 
* RawFileReader from [Planet Orbitrap](http://planetorbitrap.com/rawfilereader) or [email](https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap)  jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"
### Example
* mono RawRead.exe 171010_Ip_Hela_ugi.raw (for all scans)
* mono RawRead.exe <... rawFile> 0 2 (for profile scans with charge state > 1)
#### Compare results
```bash
awk -F '\t' '{print $1" "$6}' 171010_Ip_Hela_ugi.rawCombined/combined/txt/proteinGroups.txt | less
awk -F '\t' '{print $16}' 171010_Ip_Hela_ugi.raw.intensity0.charge0-comet-human.txt | less

## inspect DLLs
mcs InspectThermoDlls.cs -out:InspectThermoDlls.exe
mono InspectThermoDlls.exe .

## rawPeakInspector.cs

Purpose:
Dump high intensity centroid peaks directly from the Thermo RAW file and match theoretical isotope m/z windows for candidate monoisotopic masses. This is for checking whether the 22572.99, 22573.97, and 22576.98 anchors are just different labels of the same isotope envelope.

Compile:
mcs rawPeakInspector.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:rawPeakInspector.exe

Recommended run around the IgG apex region:
mono rawPeakInspector.exe 260629_Solveig_3_IgG.raw 22.5 29.5 10000000 500 10 12 30 0 35 22572.988641,22573.970103,22576.980046 IgG_anchor_check > inspect_log.txt 2>&1

Wider RT run if needed:
mono rawPeakInspector.exe 260629_Solveig_3_IgG.raw 22.5 45.1 10000000 500 10 12 30 0 35 22572.988641,22573.970103,22576.980046 IgG_anchor_check_wide > inspect_log_wide.txt 2>&1

Outputs:
IgG_anchor_check.high_intensity_peaks.tsv
IgG_anchor_check.isotope_peak_matches.tsv
IgG_anchor_check.isotope_peak_summary.tsv
IgG_anchor_check.apex_scan_windows.tsv

How to inspect:
For each candidate mass, compare isotope_peak_summary.tsv across isotope indices and charges. A wrong +n isotope anchor will shift the same raw peaks into lower isotope indices. The true monoisotopic anchor is usually the lowest anchor whose isotope series is plausible and supported across charges/RT.


## deconvRaw.cs simplified apex-isotope/ppm preferred-anchor version.

Compile:
mcs deconvRaw.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:deconvRaw.exe

Run example:
mono deconvRaw.exe 260629_Solveig_3_IgG.raw 18000 25000 8 40 10 1000000 10000000 0.85 5 3 2 35 12 0 -1 -1 -1 -1 0 0.05 3 0.20 3 300 8 20.0 0.70 1.0 0.01 > log.txt 2>&1

Main change:
PreferredMonoisotopicMass is no longer selected as lowest or highest anchor. The code now carries observed isotope-envelope apex index and ppm error into feature/anchor evidence, then selects the supported anchor with the best apex-isotope agreement using expected apex ~= round(mass/1800), followed by ppm-centering. This should select ~22572.99 for IgG and ~22574.01 for 3_L.

New diagnostic columns include WeightedAbsPpmError, ObservedApexIsotopeIndex, ExpectedApexIsotopeIndex, and ApexIsotopeDelta.

### Output to check:
*.discovery.deconv_masses.tsv


## countIons (per-scan TSV + targeted TIC accumulation)

This repository includes `countIons.cs` (compiled to `countIons.exe`) — a helper that writes a compact per-scan TSV next to a Thermo RAW file and can accumulate targeted TIC values from a user-supplied CSV of targets.

Features (current implementation)
- Writes a per-scan summary: <rawfile>.cI.tsv (columns include scan, BasePeakMass, TIC, title, time, etc.). If the .cI.tsv already exists and appears complete it will be reused instead of re-scanning the RAW.
- Reads a targets CSV (must contain a column named exactly "Mass [m/z]") and accumulates TIC for scans whose precursor m/z (parsed from the scan title) matches a target.
- Matching rules:
	- Observed m/z is parsed from the scan `title` (e.g. the "1120.0691" in "... 1120.0691@hcd27.00 ..."). The code does NOT use BasePeakMass for matching.
	- Mass matching uses an absolute tolerance of 0.0001 (i.e. |observed - target| <= 0.0001).
	- If the target CSV contains columns `Start [min]` and `End [min]`, the scan's retention time must fall within [Start - 0.01, End + 0.01] minutes (0.01 min time tolerance) to be counted.
- Output:
	- Accumulation CSV: <rawfilename>.<csv-basename> (comma-separated). It contains the original CSV fields plus columns: AccumulatedTIC, MatchedCount, MatchedMasses, MatchedTimes. Note: `MatchedMasses` now contains the scan identifier(s) (scan number) from the per-scan TSV, joined by semicolons.
	- Duplicate report: <raw>.<csv-basename>.duplicated_scans.tsv — lists scans that matched more than one target; includes the full per-scan row and full target CSV rows for each matching target.
	- Unmatched report: <raw>.<csv-basename>.unmatched_scans.tsv — lists per-scan rows that did not match any target.
	- A summary is printed to the console after processing with counts of matched / unmatched scans and report paths.

Building
1. Compile with the Mono C# compiler (same RawFileReader references as above):

```bash
mcs countIons.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll \
	/reference:ThermoFisher.CommonCore.Data.dll \
	/reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll \
	/reference:MathNet.Numerics.dll /reference:System.Numerics.dll -out:countIons.exe
```

Running
```bash
mono countIons.exe <path/to/file.raw> <path/to/targets.csv>
```
- The second argument `<targets.csv>` is optional; if omitted the code will look for a file named `targeted peptides inkl mito sched.csv` in the current directory or next to the raw file.

Target CSV format
- The only required column is `Mass [m/z]` (case-insensitive match of header). Optional columns supported: `Start [min]` and `End [min]` (numeric minutes). Other columns are preserved in the accumulation CSV output.

Minimal CSV requirements
- Required: a header row with a column named exactly `Mass [m/z]` (case-insensitive). Each data row must contain the numeric m/z value for that target.
- Optional but recommended: `Start [min]` and `End [min]` columns (numeric minutes) if you want retention-time windows for targets.
- Format: CSV text (comma-separated, UTF-8 or ASCII). Header names are matched case-insensitively and trimmed of surrounding whitespace.
- Filenames: when passing a second argument to `countIons.exe` it must be an existing `.csv` file; otherwise the program looks for `targeted peptides inkl mito sched.csv` next to the RAW file or in the current directory.
- Minimal practical example (CSV):

```
Mass [m/z],Start [min],End [min],Peptide
1120.0691,12.5,13.0,PEPTIDE_A
900.4512,,,PEPTIDE_B
```


Target CSV columns (explicit)
- Required:
	- `Mass [m/z]` — the target monoisotopic m/z (header matched case-insensitively). This column is used for mass matching against the precursor m/z parsed from the scan title.
- Optional (used for time-window filtering):
	- `Start [min]` — start of the retention-time window in minutes.
	- `End [min]` — end of the retention-time window in minutes.
- Notes:
	- Header matching is case-insensitive and trims whitespace, but column names should be spelled exactly as above for clarity.
	- Any additional columns present in the CSV are preserved verbatim and copied into the accumulation CSV output; they are not otherwise interpreted by `countIons.cs`.
	- If `Start [min]`/`End [min]` are omitted for a target, no time filtering is applied for that target (only mass matching).

	Warning
	- If your target CSV omits `Start [min]` and `End [min]` for one or more targets, those targets will be matched across the entire run (subject only to the mass tolerance). This can produce many matches or apparent "double-counting" of TIC across targets; check the `<raw>.<csv-basename>.duplicated_scans.tsv` report to find scans that were assigned to multiple targets. If you expect narrow time windows, include `Start [min]`/`End [min]` for each target.
# RawRead tools

Short, practical README for building and using the tools in this repo.

## Prerequisites

- Mono (for compiling/running on Linux)
- Thermo RawFileReader and supporting assemblies (ThermoFisher.CommonCore.RawFileReader.dll, ThermoFisher.CommonCore.Data.dll, ThermoFisher.CommonCore.MassPrecisionEstimator.dll). These are available from Planet Orbitrap (request access if needed).

## Compile (example)

Linux (Mono C# compiler):

```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll \
	/reference:ThermoFisher.CommonCore.Data.dll \
	/reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll \
	/reference:MathNet.Numerics.dll /reference:System.Numerics.dll
```

Windows: use the MS C# compiler (csc) or a suitable .NET toolchain.

## countIons — per-scan TSV and targeted TIC accumulation

`countIons.cs` (compiled to `countIons.exe`) writes a compact per-scan TSV next to a RAW file (`<raw>.cI.tsv`) and can accumulate targeted TIC values from a targets CSV.

Note: the older `RawRead.cs` tool is still included in this repository for other extraction/search workflows; `countIons.cs` is an additional helper focused on compact per-scan tables and targeted accumulation.

Key behavior
- Writes per-scan TSV: `<raw>.cI.tsv` (scan, BasePeakMass, TIC, title, time, etc.). If an apparently-complete `.cI.tsv` exists the program will reuse it instead of re-scanning the RAW.
- Reads a targets CSV (header must include `Mass [m/z]`). Optional `Start [min]` and `End [min]` columns are supported and used as RT windows.
- Matching rules (current defaults):
	- Observed m/z is parsed from the scan `title` (e.g. `1120.0691` in `... 1120.0691@hcd27.00 ...`). The code does NOT use BasePeakMass for matching.
	- Mass tolerance: absolute difference <= 0.0001.
	- Time window: if `Start [min]`/`End [min]` are present, the scan RT must be inside [Start - 0.01, End + 0.01] minutes.

- Because `countIons.cs` only uses the precursor m/z parsed from the collected scan title (and an absolute mass tolerance of 0.0001), it cannot reliably discriminate different peptide sequences that share the same monoisotopic mass within that tolerance when their retention-time windows overlap. For example, two peptides with identical or near-identical monoisotopic mass (e.g., "LSLAQEDLISNR" vs "GSLLLGGLDAEASR" in a hypothetical case) will both be counted for the same scan if the scan's RT falls inside both targets' Start/End windows. If you need sequence-level disambiguation you should use additional information (e.g., MS2 fragment matching, narrower mass/time tolerances, or peptide-specific markers) rather than title-only m/z matching.

Note about accumulated intensities
- In the present implementation, when a single scan matches multiple targets the scan's TIC is added to each matching target's AccumulatedTIC. In other words, the same ion intensity can be "double-counted" (or counted multiple times) across targets. All such cases are listed in the `<raw>.<csv-basename>.duplicated_scans.tsv` report so you can find and inspect duplicated assignments.
- Because `countIons.cs` only uses the precursor m/z parsed from the collected scan title (and an absolute mass tolerance of 0.0001), it cannot reliably discriminate different peptide sequences that share the same monoisotopic mass within that tolerance when their retention-time windows overlap. For example, two peptides with identical or near-identical monoisotopic mass (e.g., "LSLAQEDLISNR" vs "GSLLLGGLDAEASR" in a hypothetical case) will both be counted for the same scan if the scan's RT falls inside both targets' Start/End windows. If you need sequence-level disambiguation you should use additional information (e.g., MS2 fragment matching, narrower mass/time tolerances, or peptide-specific markers) rather than title-only m/z matching.

Future work
- A natural next step is to actually inspect MS2 fragment spectra (fragment ion matching) to disambiguate peptides that share precursor m/z. Implementing fragment-based matching would allow sequence-level confirmation (for example by matching theoretical fragment ions or using a lightweight search engine) and avoid counting ambiguous precursors based on title-only m/z. This is planned as a future enhancement.

Outputs
- Accumulation CSV: `<raw>.<csv-basename>` — contains the original target CSV fields plus `AccumulatedTIC,MatchedCount,MatchedMasses,MatchedTimes`. `MatchedMasses` lists the per-scan identifiers (scan numbers) joined by semicolons.
- Duplicates report: `<raw>.<csv-basename>.duplicated_scans.tsv` — scans matched to more than one target; includes the full per-scan row and the full target CSV rows for each matching target.
- Unmatched report: `<raw>.<csv-basename>.unmatched_scans.tsv` — per-scan rows with no matching target.
- A concise match summary is printed to the console (counts and report paths).

Build and run example

```bash
mcs countIons.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll \
	/reference:ThermoFisher.CommonCore.Data.dll \
	/reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll \
	/reference:MathNet.Numerics.dll /reference:System.Numerics.dll -out:countIons.exe

mono countIons.exe /path/to/file.raw /path/to/targets.csv
```

Notes
- If you pass a second argument that either doesn't exist or is not a `.csv` file the program will print an explicit error and exit with non-zero status.
- Tolerances (mass: 0.0001, time: 0.01 min) are hard-coded, but can be exposed as CLI flags on request.
- If you want `MatchedMasses` to contain other identifiers (e.g., `scan:rt`) or prefer different separators/file naming, tell me and I will update `countIons.cs`.

Questions / next steps
- Want the tolerances to be CLI-configurable? Rename `MatchedMasses` to `MatchedScans`? Change separators? I can change any of these quickly.

## RawRead.cs (legacy extractor)

`RawRead.cs` is an older, more featureful extractor included in this repository. It is released under GPL v2+ and provides a broad set of RAW-to-text/format conversions and diagnostics. Below is a compact description to help you pick which tool to use.

What it does
- Reads Thermo RAW files via ThermoFisher.CommonCore.RawFileReader and prints run metadata.
- Estimates mass precision for MS1 scans and writes `<raw>.MZ.txt`.
- Extracts BasePeak chromatogram to `<raw>.chromatogram.txt`.
- Writes centroid MS2 MGF (`<raw>.centroid.MGF`) and profile MGF (`<raw>.profile.MGF`) depending on scan type.
- Writes per-scan profile dumps to `<raw>.profile.intensity{insThr}.charge{chgThr}.MS.txt` and an FFT summary `<raw>.intensity{insThr}.charge{chgThr}.FFT.txt`.

How to build
```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll \
	/reference:ThermoFisher.CommonCore.Data.dll \
	/reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll \
	/reference:MathNet.Numerics.dll /reference:System.Numerics.dll
```

How to run
```bash
mono RawRead.exe /path/to/file.raw [intensityThreshold] [chargeThreshold]
```

Key outputs (examples)
- `<raw>.MZ.txt` — mass precision estimates (Mass, mmu, ppm)
- `<raw>.chromatogram.txt` — BasePeak chromatogram (RT, intensity)
- `<raw>.centroid.MGF`, `<raw>.profile.MGF` — MS2 blocks for centroid/profile scans
- `<raw>.profile.intensity{insThr}.charge{chgThr}.MS.txt` — per-scan profile/intensity listing
- `<raw>.intensity{insThr}.charge{chgThr}.FFT.txt` — FFT-derived summary

Notable behaviors & caveats
- Uses `GetReaction(0).PrecursorMass` for PEPMASS in MGF; some scans may lack reactions — code assumes they exist.
- Extracts charge from trailer labels by matching `"Charge State:"` — trailer labeling may vary by instrument/firmware.
- Heuristic branches (e.g., `title.Contains(" ms ")`) determine some output formats; these heuristics may not be universal.
- Large files: the code keeps arrays sized by the number of scans and may use significant memory for very long runs.

Improvement suggestions
- Add defensive null/index checks when accessing reactions or trailer fields.
- Use `using` blocks for all writers to ensure file handles are always closed on exceptions.
- Make thresholds, tolerances and output paths CLI-configurable (named flags).
- Consider writing a compact per-scan TSV (like `countIons.cs`) and reuse it to avoid repeated RAW scanning.

If you want, I can make a small patch to `RawRead.cs` to (a) add a safe per-scan TSV writer, (b) expose CLI flags for thresholds, or (c) harden trailer/reaction access. Tell me which change to prioritize.

awk -F '\t' '{print $16}' 171010_Ip_Hela_ugi.raw.intensity0.charge0-comet-human.txt | less
```


