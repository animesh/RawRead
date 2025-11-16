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
```

Note: the `RawRead.cs` in this repository for other extraction of all info in raw file; `countIons.cs` is an additional helper focused on compact per-scan tables and targeted accumulation.

# RawRead

`RawRead.cs` contains the C# code for extraction of all info in raw file

## Compile 

Linux (Mono C# compiler):

```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll \
	/reference:ThermoFisher.CommonCore.Data.dll \
	/reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll \
	/reference:MathNet.Numerics.dll /reference:System.Numerics.dll
```

Windows: use the MS C# compiler (csc) or a suitable .NET toolchain.

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

## Also contains *countIons* tool — per-scan TSV and targeted TIC accumulation

`countIons.cs` is an additional helper focused on compact per-scan tables and targeted accumulation. `countIons.cs` (compiled to `countIons.exe`) writes a compact per-scan TSV next to a RAW file (`<raw>.cI.tsv`) and can accumulate targeted TIC values from a targets CSV.

Build and run example

```bash
mcs countIons.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll \
	/reference:ThermoFisher.CommonCore.Data.dll \
	/reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll \
	/reference:MathNet.Numerics.dll /reference:System.Numerics.dll -out:countIons.exe

mono countIons.exe /path/to/file.raw /path/to/targets.csv
```

Note
- Tolerances (mass: 0.0001, time: 0.01 min) are hard-coded, but can be exposed as CLI flags on request.

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

Outputs
- Accumulation CSV: `<raw>.<csv-basename>` — contains the original target CSV fields plus `AccumulatedTIC,MatchedCount,MatchedMasses,MatchedTimes`. `MatchedMasses` lists the per-scan identifiers (scan numbers) joined by semicolons.
- Duplicates report: `<raw>.<csv-basename>.duplicated_scans.tsv` — scans matched to more than one target; includes the full per-scan row and the full target CSV rows for each matching target.
- Unmatched report: `<raw>.<csv-basename>.unmatched_scans.tsv` — per-scan rows with no matching target.
- A concise match summary is printed to the console (counts and report paths).

Future work
- A natural next step is to actually inspect MS2 fragment spectra (fragment ion matching) to disambiguate peptides that share precursor m/z. Implementing fragment-based matching would allow sequence-level confirmation (for example by matching theoretical fragment ions or using a lightweight search engine) and avoid counting ambiguous precursors based on title-only m/z. This is planned as a future enhancement.

