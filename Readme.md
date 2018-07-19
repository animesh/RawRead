### Prerequisites
RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader or email jim.shofstahl@thermofisher.com with Subject "Access to RawFileReader"  

##Compile on Linux
mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll /reference:ThermoFisher.CommonCore.Data.dll

#Windows
"csc" instead of "msc" above

##Usage
(mono) RawRead.exe RawFileName intensity-threshold charge-threshold 

#Example
mono RawRead.exe BSA.raw 0 0 (for all scans)
mono RawRead.exe BSA.raw 0 2 (for scans with charge state > 1)
mono RawRead.exe BSA.raw 10e6 0 (for scans with recorded intensity > 1 million)


