//released under GPL version 2 or later: sharma.animesh@gmail.com
//install mono and compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
//run: mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
//windows with dotnet: c:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
namespace RawRead
{
    using ThermoFisher.CommonCore.RawFileReader;//RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader
    using ThermoFisher.CommonCore.Data.Business;//RawFileReader
    using MathNet.Numerics.Statistics;//nuget https://numerics.mathdotnet.com/ or dotnet https://www.nuget.org/packages/MathNet.Numerics/
    using System;
    using System.IO;
    using System.Collections.Generic;
    //    using System.Linq;
    //    using System.Xml.Linq;

    internal class RawRead2PeakList
    {
        static void Main(string[] args)
        {
            var rawFile = RawFileReaderAdapter.FileFactory("171010_Ip_Hela_ugi.raw");
            //var rawFile = RawFileReaderAdapter.FileFactory("20150512_BSA_The-PEG-envelope.raw");
            //var rawFile = RawFileReaderAdapter.FileFactory("20200219_KKL_SARS_CoV2_pool1_F1.raw");
            string timeStart = DateTime.Now.ToString("yyyyMMddHHmmss");
            if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("\nUSAGE: {0} *FILENAME* Threshold4intensity(default 0) Threshold4errorTolerance(default 3 for precision upto 3 decimal points) hmdbFile(default is hmdb_metabolites.sample.xml)\n\n :using default FILENAME:171010_Ip_Hela_ugi.raw\n", AppDomain.CurrentDomain.FriendlyName); }
            if (args.Length > 0) { rawFile = RawFileReaderAdapter.FileFactory(args[0]); }
            if (!rawFile.IsOpen) { Console.WriteLine("Raw file {1} is already Open, probably not finish writing", rawFile.FileError, args[0]); return; }
            if (rawFile.IsError) { Console.WriteLine("Error opening {1}, probably not proper orbitrap raw file? This program is tested only on Elite, QE and HF orbitrap raw files...", rawFile.FileError, args[0]); return; }
            long insThr = 0;
            if (args.Length == 2) { insThr = long.Parse(args[1]); }
            int errTol = 3; // 445.12057 10ppm[445.11612-445.12502]
            if (args.Length == 3) { errTol = int.Parse(args[2]); }
            string hmdbFile = "hmdb_metabolites.sample.xml";//Sample from Unzipped Metabolite and Protein Data (in XML format) https://hmdb.ca/downloads 
            if (args.Length == 4) { hmdbFile = args[3]; }
            string fileMS1 = rawFile.FileName + ".intensityThreshold" + insThr + ".errTolDecimalPlace" + errTol + ".Time" + timeStart + hmdbFile + ".MS.csv";
            string fileMZ1R = rawFile.FileName + ".intensityThreshold" + insThr + ".errTolDecimalPlace" + errTol + ".Time" + timeStart + hmdbFile + ".MZ1R.csv";
            rawFile.SelectInstrument(Device.MS, 1);
            int fMS = rawFile.RunHeaderEx.FirstSpectrum;
            int nMS = rawFile.RunHeaderEx.LastSpectrum;
            var fFilter = rawFile.GetFilterForScanNumber(fMS);
            var nFilter = rawFile.GetFilterForScanNumber(nMS);
            //GetSpectrum(rawFile, fMS, fFilter.ToString(), false);
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName + "\n" + "#prescan(s):\t" + nMS + "\n" + "#RT length:\t" + (eTime - sTime) + "\n" + "#version:\t" + rawFile.FileHeader.Revision + "\n" + "#create date:\t" + rawFile.FileHeader.CreationDate + "\n" + "#machine:\t" + rawFile.FileHeader.WhoCreatedId + "\n" + "#serial:\t" + rawFile.GetInstrumentData().SerialNumber + "\n" + "#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion + "\n" + "#resolution:\t" + rawFile.RunHeaderEx.MassResolution + "\n" + "#massrange:\t" + rawFile.RunHeaderEx.LowMass + "-" + rawFile.RunHeaderEx.HighMass + "\n" + "#sample:\t" + rawFile.SampleInformation.Vial + "\n" + "#volume:\t" + rawFile.SampleInformation.SampleVolume + "\n" + "#injection:\t" + rawFile.SampleInformation.InjectionVolume + "\n" + "#dilution:\t" + rawFile.SampleInformation.DilutionFactor + "\n" + "#filter:\t" + fFilter.ToString() + "\n" + "#filterN:\t" + nFilter.ToString());
        }
    }
}
