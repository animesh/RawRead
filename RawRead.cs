//released under GPL version 2 or later: sharma.animesh@gmail.com 
//install mono and compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll /reference:ThermoFisher.CommonCore.Data.dll
//run: mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)

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
            if (args.Length < 1) { Console.WriteLine("USAGE: {0} fileName intensityThreshold(optional) chargeThreshold(optional)", AppDomain.CurrentDomain.FriendlyName); return; }
            var rawFile = RawFileReaderAdapter.FileFactory(args[0]);
            double insThr = 0;
            int chgThr = 0;
            if (args.Length == 2) { insThr = double.Parse(args[1]); }
            if (args.Length == 3) { insThr = double.Parse(args[1]); chgThr = int.Parse(args[2]); }
            rawFile.SelectInstrument(Device.MS, 1);
            int fms = rawFile.RunHeaderEx.FirstSpectrum;
            int nms = rawFile.RunHeaderEx.LastSpectrum;
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName + "\n" + "#prescan(s):\t" + nms + "\n" + "#RT length:\t" + (eTime - sTime) + "\n" + "#version:\t" + rawFile.FileHeader.Revision + "\n" + "#create date:\t" + rawFile.FileHeader.CreationDate + "\n" + "#machine:\t" + rawFile.FileHeader.WhoCreatedId + "\n" + "#serial:\t" + rawFile.GetInstrumentData().SerialNumber + "\n" + "#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion + "\n" + "#resolution:\t" + rawFile.RunHeaderEx.MassResolution + "\n" + "#massrange:\t" + rawFile.RunHeaderEx.LowMass + "-" + rawFile.RunHeaderEx.HighMass);
            int tms = 0;
            double tic = 0;
            double maxIntSum = 0;
            Complex[] samples = new Complex[nms];
            StreamWriter writeMS1 = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".charge" + chgThr + "." + "MS.txt");
            StreamWriter writeMS2 = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".charge" + chgThr + "." + "MSMS.txt");
            for (int i = fms; i < nms; i++)
            {
                var centroidStream = rawFile.GetCentroidStream(i, true);
                var profileStream = rawFile.GetSimplifiedScan(i);
                double time = rawFile.RetentionTimeFromScanNumber(i);
                string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(i));
                if (title.Contains("ms2"))
                {
                    writeMS2.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}", i, title, centroidStream.Length, profileStream, time);
                    for (int j = 0; j < centroidStream.Length; j++)
                    {
                        writeMS2.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}", i, title, centroidStream.Length, j, centroidStream.Masses[j], centroidStream.Intensities[j], centroidStream.Charges[j], centroidStream.Masses[j] * centroidStream.Charges[j] - centroidStream.Charges[j]);
                    }
                }
                double maxMass = 0;
                double maxInt = 0;
                if (title.Contains("Full ms "))
                {
                    for (int j = 0; j < centroidStream.Length; j++)
                    {
                        if (centroidStream.Charges[j] >= chgThr && centroidStream.Intensities[j] >= insThr)
                        {
                            writeMS1.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}", i, title, centroidStream.Length, j, centroidStream.Masses[j], centroidStream.Intensities[j], centroidStream.Charges[j], centroidStream.Masses[j] * centroidStream.Charges[j] - centroidStream.Charges[j]);
                        }
                        if (centroidStream.Intensities[j] >= maxInt) { maxInt = centroidStream.Intensities[j]; maxMass = centroidStream.Masses[j]; maxIntSum += maxInt; }
                        tic += centroidStream.Intensities[j]; tms++;
                    }
                }
                samples[i] = new Complex(maxMass, 0);
                Console.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t", i, maxIntSum, title, centroidStream.Length, maxMass, time, maxInt);
            }
            Fourier.Forward(samples, FourierOptions.NoScaling);
            StreamWriter writeFFT = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".charge" + chgThr + "." + "FFT.txt");
            for (int i = fms; i < nms; i++)
            {
                double magnitude = (2.0 / nms) * (Math.Abs(Math.Sqrt(Math.Pow(samples[i].Real, 2) + Math.Pow(samples[i].Imaginary, 2))));
                double angle = Math.Atan(samples[i].Imaginary / samples[i].Real);
                writeFFT.WriteLine("{0}\t{1}\t{2}", i, magnitude, angle);
            }
            Console.WriteLine("#TIC>={0}intensity:\t{1}", insThr, tic);
            Console.WriteLine("#Ions>=charge{0}:\t{1}", chgThr, tms);
        }
    }
}
