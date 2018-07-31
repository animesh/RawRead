# Prerequisites
* [Mono](http://www.mono-project.com/download/stable/#download-lin) 
* RawFileReader from [Planet Orbitrap](http://planetorbitrap.com/rawfilereader) or [email](https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap)  jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"

## Compile
```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
```

## Usage
```bash
mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
```

### Example
* mono RawRead.exe 171010_Ip_Hela_ugi.raw (for all scans)
* mono RawRead.exe <... rawFile> 0 2 (for scans with charge state > 1)
* mono RawRead.exe <... rawFile> 10e6(for scans with recorded intensity > 1 million)
* mono RawRead.exe <... rawFile> 10e6 2 (for scans with charge state > 1 AND recorded intensity > 1 million)

... 

### Test

#### Search converted file with [comet-ms](https://sourceforge.net/projects/comet-ms/)
* normal window search for Qexactive(HF) and [Canonical Human Reviewed Database](https://www.uniprot.org/uniprot/?query=proteome:UP000005640%20reviewed:yes#)
```bash
./comet.2018012.linux.exe 171010_Ip_Hela_ugi.raw.intensity0.charge0.MGF
```

#### Search Raw file directly since [MaxQuant goes Linux](https://www.nature.com/articles/s41592-018-0018-y), we can finally perform a direct search :) though there is an annoying issues of reproducibility in comparison with Windows run. It differs by ~1% which (has been reported to developers)[https://maxquant.myjetbrains.com/youtrack/issue/MaxQuant-185] and there seems to be an inherent problem in the way mono handles numbers c.f. dotnet , anyways 1% is something one can live with ;)

* Download and Install [MaxQuant](http://www.coxdocs.org/doku.php?id=maxquant:common:download_and_installation)
* create a parameter file for all raw files in the directory using the provided generic parameter file mqparTest.xml
```bash
for i in  *.raw; do echo $i; sed -e "s|RawTestFile|/$PWD/$i|g" mqparTest.xml > $i.mqpar.xml ; done
```
* use the paramter file(s) to search in [parallel](https://www.gnu.org/software/parallel/) 
```bash
find . -name "*.mqpar.xml" | parallel "mono <MaxQuant_1.6.2.3 root directory>/MaxQuant/bin/MaxQuantCmd.exe  {}" 2>stdout2 1>stdout1 > stdout0 &
tail -f stdout?
```

#### Compare results
```bash
awk -F '\t' '{print $1" "$6}' 171010_Ip_Hela_ugi.rawCombined/combined/txt/proteinGroups.txt | less
awk -F '\t' '{print $16}' 171010_Ip_Hela_ugi.raw.intensity0.charge0-comet-human.txt | less
```


