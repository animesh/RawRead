```bash
for i in ../Atle/*.raw ; do echo $i; ./RawRead.exe $i | grep sample ; done
```

# Prerequisites
* [Mono](http://www.mono-project.com/download/stable/#download-lin) 
* RawFileReader from [Planet Orbitrap](http://planetorbitrap.com/rawfilereader) or [email](https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap)  jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"

## Compile

### Linux

```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
```


#### Windows can also use dotnet cmd "csc"

```bash
c:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
```

## Usage
```bash
mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
```

##### Windows compiled via dotnet call directly
```bash
RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
```


### Example
* mono RawRead.exe 171010_Ip_Hela_ugi.raw (for all scans)
* mono RawRead.exe <... rawFile> 0 2 (for profile scans with charge state > 1)
* mono RawRead.exe <... rawFile> 10e6(for profile scans with recorded intensity > 1 million)
* mono RawRead.exe <... rawFile> 10e6 2 (for profile scans with charge state > 1 AND recorded intensity > 1 million)

... 



