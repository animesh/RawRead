# Prerequisites
* [Mono](http://www.mono-project.com/download/stable/#download-lin) 
* RawFileReader from [Planet Orbitrap](http://planetorbitrap.com/rawfilereader) or [email](https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap)  jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"

## Compile
```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll /reference:ThermoFisher.CommonCore.Data.dll
```

## Usage
```bash
mono RawRead.exe fileName intensityThreshold chargeThreshold
```

### Example
* mono RawRead.exe <Path to Raw File> 0 0 (for all scans)
* mono RawRead.exe <... rawFile> 0 2 (for scans with charge state > 1)
* mono RawRead.exe <... rawFile> 10e6 0 (for scans with recorded intensity > 1 million)
* mono RawRead.exe <... rawFile> 10e6 2 (for scans with charge state > 1 AND recorded intensity > 1 million)

... 


