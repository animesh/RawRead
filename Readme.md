# Prerequisites
* OS [check](https://dotnet.microsoft.com/learn/aspnet/blazor-tutorial/create)

```bash
cat /etc/os-release 
dotnet --list-runtimes
dotnet --list-sdk
```
* setup ubuntu-16.04 [dotnet](https://docs.microsoft.com/en-in/dotnet/core/install/linux-ubuntu#1604-)

```bash
wget https://packages.microsoft.com/config/ubuntu/16.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update;  sudo apt-get install -y apt-transport-https &&  sudo apt-get update &&  sudo apt-get install -y dotnet-sdk-5.0
git clone http://github.com/animesh/RawRead
git checkout -b blazor
cat RawRead.csproj  >> BlazorApp.csproj
rm -rf bin obj
sed -i "s|http://localhost:5000|http://10.20.93.118:8080|g" Properties/launchSettings.json
```

* launch 

```bash
dotnet run #open 10.20.93.253:8080
```

## Compile

* [Mono](http://www.mono-project.com/download/stable/#download-lin) 
* RawFileReader from [Planet Orbitrap](http://planetorbitrap.com/rawfilereader) or [email](https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap)  jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"

### Linux

```bash
mcs RawRead.code /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
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

### Test

#### Search converted file with [comet-ms](https://sourceforge.net/projects/comet-ms/)
* normal window search for Qexactive(HF) and [Canonical Human Reviewed Database](https://www.uniprot.org/uniprot/?query=proteome:UP000005640%20reviewed:yes#)
```bash
./comet.2018012.linux.exe 171010_Ip_Hela_ugi.raw.intensity0.charge0.MGF
```

###  Search Raw file directly in linux
since [MaxQuant goes Linux](https://www.nature.com/articles/s41592-018-0018-y), we can finally perform a direct search :) though there is an annoying issues of reproducibility in comparison with Windows run. It differs by ~1% which (has been reported to developers)[https://maxquant.myjetbrains.com/youtrack/issue/MaxQuant-185] and there seems to be an inherent problem in the way mono handles numbers c.f. dotnet , anyways 1% is something one can live with ;)

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


