//released under GPL version 2 or later: sharma.animesh@gmail.com
//install mono and compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
//run: mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
//windows with dotnet: c:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
namespace RawRead
{
    using ThermoFisher.CommonCore.RawFileReader;//RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader
    using ThermoFisher.CommonCore.Data.Business;//RawFileReader
    using MathNet.Numerics.IntegralTransforms;//nuget or dotnet https://www.nuget.org/packages/MathNet.Numerics/
    using System.Numerics;//https://www.nuget.org/packages/System.Runtime.Numerics/
    using System;
    using System.IO;

    internal class RawRead2PeakList
    {
        static void Main(string[] args)
        {
            var rawFile = RawFileReaderAdapter.FileFactory("20150512_BSA_The-PEG-envelope.raw");
            if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("USAGE: {0} fileName intensityThreshold(optional) chargeThreshold(optional), using default 171010_Ip_Hela_ugi.raw", AppDomain.CurrentDomain.FriendlyName);  }
            if(args.Length > 0){ rawFile = RawFileReaderAdapter.FileFactory(args[0]);}
            if(!rawFile.IsOpen) { Console.WriteLine("Raw file {1} is already Open, probably not finish writing", rawFile.FileError, args[0]); return; }
            if(rawFile.IsError) { Console.WriteLine("Error opening {1}, probably not proper orbitrap raw file? Tested only on Elite, QE and HF...", rawFile.FileError, args[0]); return; }
            double insThr = 50e5;
            if (args.Length == 2) { insThr = double.Parse(args[1]); }
            rawFile.SelectInstrument(Device.MS, 1);
            int fMS = rawFile.RunHeaderEx.FirstSpectrum;
            int nMS = rawFile.RunHeaderEx.LastSpectrum;
            var fFilter = rawFile.GetFilterForScanNumber(fMS);
            var nFilter = rawFile.GetFilterForScanNumber(nMS);
            //GetSpectrum(rawFile, fMS, fFilter.ToString(), false);
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName + "\n" + "#prescan(s):\t" + nMS + "\n" + "#RT length:\t" + (eTime - sTime) + "\n" + "#version:\t" + rawFile.FileHeader.Revision + "\n" + "#create date:\t" + rawFile.FileHeader.CreationDate + "\n" + "#machine:\t" + rawFile.FileHeader.WhoCreatedId + "\n" + "#serial:\t" + rawFile.GetInstrumentData().SerialNumber + "\n" + "#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion + "\n" + "#resolution:\t" + rawFile.RunHeaderEx.MassResolution + "\n" + "#massrange:\t" + rawFile.RunHeaderEx.LowMass + "-" + rawFile.RunHeaderEx.HighMass + "\n" + "#sample:\t" + rawFile.SampleInformation.Vial + "\n" + "#volume:\t" + rawFile.SampleInformation.SampleVolume + "\n" + "#injection:\t" + rawFile.SampleInformation.InjectionVolume + "\n" + "#dilution:\t" + rawFile.SampleInformation.DilutionFactor + "\n" + "#filter:\t" + fFilter.ToString() + "\n" + "#filterN:\t" + nFilter.ToString());
            int tms = 0;
            double tic = 0;
            double maxIntSum = 0;
            Complex[] samples = new Complex[nMS];
            StreamWriter writeMS1 = new StreamWriter(rawFile.FileName + ".profile.intensity" + insThr +  ".MS.txt");
            //writeMS1.WriteLine("Scan\tBasePeak\tRT\tMaxIntensity\tCumulativeIntensity");
            for (int i = fMS; i <= nMS; i++)
            {
                double time = rawFile.RetentionTimeFromScanNumber(i);
                string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(i));
                var scanStatistics = rawFile.GetScanStatsForScanNumber(i);
                var centroidStream = rawFile.GetCentroidStream(i, false);
                double maxInt = 0;
                if (title.Contains(" ms ") && scanStatistics.BasePeakIntensity>insThr)
                {
                    for (int j = 0; j < centroidStream.Length; j++)
                    {
                        tic += centroidStream.Intensities[j];
                        tms++;
                        if (centroidStream.Intensities[j] >= maxInt) { maxInt = centroidStream.Intensities[j]; maxIntSum += maxInt; }
                        writeMS1.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}", tms, time, i, j, centroidStream.Masses[j], centroidStream.Intensities[j], scanStatistics.BasePeakMass, maxInt, tic, maxIntSum);
                    }
                    //writeMS1.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}", i, scanStatistics.BasePeakMass, time,maxInt,maxIntSum);
                    samples[i-1] = new Complex(scanStatistics.BasePeakMass,time);
                if(i>1&&Math.Ceiling(samples[i-1].Real)<samples[i-2].Real){Console.WriteLine("{0}\t{1}\t{2}\t{3}", i, scanStatistics.BasePeakMass, maxIntSum,samples[i-2].Real-samples[i-1].Real);}
                }
            }
            Fourier.Forward(samples, FourierOptions.NoScaling);
            StreamWriter writeFFT = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".FFT.txt");
            writeFFT.WriteLine("MZ\tRT\tangle\tmagnitude");
            for (int i = fMS-1; i < nMS; i++)
            {
                double magnitude = (2.0 / nMS) * (Math.Abs(Math.Sqrt(Math.Pow(samples[i].Real, 2) + Math.Pow(samples[i].Imaginary, 2))));
                double angle = Math.Atan(samples[i].Imaginary / samples[i].Real);
                writeFFT.WriteLine("{0}\t{1}\t{2}\t{3}", samples[i].Real, samples[i].Imaginary, angle, magnitude);
            }
            Console.WriteLine("#TIC>={0}intensity:\t{1}\t{2}", insThr, tms, tic);
            rawFile.Dispose();
            writeMS1.Close();
            writeFFT.Close();
        }
    }
}
