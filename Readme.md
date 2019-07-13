# Prerequisites

RawFileReader from [Planet Orbitrap](http://planetorbitrap.com/rawfilereader) or [email](https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap)  jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"


## Compile
* [Mono](http://www.mono-project.com/download/stable/#download-lin) 
* or [Dotnet] https://docs.microsoft.com/en-in/dotnet/csharp/roslyn-sdk/ 

```bash
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll /reference:CometWrapper.dll
```

OR 

```bash
c:\Windows\Microsoft.NET\Framework\v4.0.30319\msbuild RawRead.csproj
```

## Index the fasta file
*fasta.idx* (above crap.fasta.idx) is generated via comet.exe which also needs to be [compiled](https://sourceforge.net/code-snapshots/svn/c/co/comet-ms/code/comet-ms-code-r1296-trunk-comet-ms.zip) and the fasta file needs to be provided via the comet.param* file; crap.fasta mentioned in comet.param in this is compiled from [cRAP](https://www.thegpm.org/crap/) and [MaxQuant](https://www.maxquant.org/)

```bash
./comet.exe -i
```

## Usage

```bash
RawRead.exe 171010_Ip_Hela_ugi.raw crap.fasta.idx
```

### Real-time comet search 

Source: [Full-featured, real-time database searching platform enables fast and accurate multiplexed quantitative proteomics](https://www.biorxiv.org/content/10.1101/668533v1)

* Browse the trunk code at revision 1296 as suggested by Jimmy in the [forum](https://groups.google.com/forum/?utm_medium=email&utm_source=footer#!msg/comet-ms/VvWqGPTmRCg/RFV6T5IoCAAJ) or download as [zip](https://sourceforge.net/code-snapshots/svn/c/co/comet-ms/code/comet-ms-code-r1296-trunk-comet-ms.zip)

* Unzip/browse and generate the CometWrapper.dll mentioned in [release-note](http://comet-ms.sourceforge.net/release/release_201901/) by compiling the folder RealtimeSearch within the distribution [note: had to compile the CometWrapper and MSToolkit first though. Also had to add the LIB to MSToolkit and make sure it compiled to x64 in case of visual studio as compiler]

* Binary is hopefully generate in RealtimeSearch/bin/Debug folder which can be directly used to search a raw file against an indexed database

```bash
./RealtimeSearch/bin/Debug/RealtimeSearch.exe 171010_Ip_Hela_ugi.raw crap.fasta.idx

 RealTimeSearch

 input: 171010_Ip_Hela_ugi.raw
pass 1  600     2       595.3168        1189.6263       K.KKEDALNDTR.D  P17697 SWI      0.28    3       1/18
pass 1  800     2       479.2978        957.5883        K.RTLKVQGR.D    Q3T052 TRE      0.12    2       2/14
0       0
1       0
2       159
3       61
4       6
5       0
6       1
7       0
8       1
9       0
10      0
11      0
12      0
13      0
14      0
15      0
16      0
17      0
18      0
19      0
20      0
21      0
22      0
23      0
24      0
25      0
26      0
27      0
28      0
29      0

 Done.

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


