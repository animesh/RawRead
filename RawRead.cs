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
    using System.Linq;

    internal class RawRead2PeakList
    {
        static void Main(string[] args)
        {
            //var rawFile = RawFileReaderAdapter.FileFactory("20150512_BSA_The-PEG-envelope.raw");
            //var rawFile = RawFileReaderAdapter.FileFactory("20200219_KKL_SARS_CoV2_pool1_F1.raw");
            string timeStart = DateTime.Now.ToString("yyyyMMddHHmmss"); 
            var rawFile = RawFileReaderAdapter.FileFactory("171010_Ip_Hela_ugi.raw");
            if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("\nUSAGE: {0} *FILENAME* Threshold4intensity(default 1000) PPMthreshold(default 10 for binning around 10 parts/million mass-precision) Threshold4errorTolerance(default 3 for precision upto 3 decimal points)\n\n :using default FILENAME:171010_Ip_Hela_ugi.raw 1000intensity 10PPM 3decimal\n\n", AppDomain.CurrentDomain.FriendlyName); }
            if(args.Length > 0){ rawFile = RawFileReaderAdapter.FileFactory(args[0]);}
            if(!rawFile.IsOpen) { Console.WriteLine("Raw file {1} is already Open, probably not finish writing", rawFile.FileError, args[0]); return; }
            if(rawFile.IsError) { Console.WriteLine("Error opening {1}, probably not proper orbitrap raw file? This program is tested only on Elite, QE and HF orbitrap raw files...", rawFile.FileError, args[0]); return; }
            long insThr = 1000;
            if (args.Length == 2) { insThr = long.Parse(args[1]); }
            int ppmTol = 10; // 445.12057 ~10ppm[445.11612-445.12502]
            if (args.Length == 3) { ppmTol = int.Parse(args[2]); }
            int errTol = 3; // round to decimal, for above 445.12->445.125,445.116;445.116->445.12,445.111;445.125->445.129,445.12445.12057
            if (args.Length == 4) { errTol = int.Parse(args[3]); }
            rawFile.SelectInstrument(Device.MS, 1);
            int fMS = rawFile.RunHeaderEx.FirstSpectrum;
            int nMS = rawFile.RunHeaderEx.LastSpectrum;
            var fFilter = rawFile.GetFilterForScanNumber(fMS);
            var nFilter = rawFile.GetFilterForScanNumber(nMS);
            //GetSpectrum(rawFile, fMS, fFilter.ToString(), false);
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName + "\n" + "#prescan(s):\t" + nMS + "\n" + "#RT length:\t" + (eTime - sTime) + "\n" + "#version:\t" + rawFile.FileHeader.Revision + "\n" + "#create date:\t" + rawFile.FileHeader.CreationDate + "\n" + "#machine:\t" + rawFile.FileHeader.WhoCreatedId + "\n" + "#serial:\t" + rawFile.GetInstrumentData().SerialNumber + "\n" + "#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion + "\n" + "#resolution:\t" + rawFile.RunHeaderEx.MassResolution + "\n" + "#massrange:\t" + rawFile.RunHeaderEx.LowMass + "-" + rawFile.RunHeaderEx.HighMass + "\n" + "#sample:\t" + rawFile.SampleInformation.Vial + "\n" + "#volume:\t" + rawFile.SampleInformation.SampleVolume + "\n" + "#injection:\t" + rawFile.SampleInformation.InjectionVolume + "\n" + "#dilution:\t" + rawFile.SampleInformation.DilutionFactor + "\n" + "#filter:\t" + fFilter.ToString() + "\n" + "#filterN:\t" + nFilter.ToString());
            long tms = 0;
            long tic = 0;
            long maxIntSum = 0;
//            Complex[] samples = new Complex[nMS];
            StreamWriter writeMS1 = new StreamWriter(rawFile.FileName + ".intensityThreshold" + insThr + ".PPM" + ppmTol + ".errTolDecimalPlace" + errTol + ".Time" + timeStart + ".MS.csv");
            Console.WriteLine("ScanContainScans\tBasePeak\tIoncnt\tRetention(minutes)\tMostIntenseMass2Charge\tCumulativeIntensity\tTotalScans");
            writeMS1.WriteLine("Scan,ContainScans,BasePeak,MaxIntensity,RetentionTime,MostIntenseMass2Charge,CumulativeIntensity,TotalScans");
            Dictionary<string, long> intMZ1 = new Dictionary<string, long>();
            Dictionary<string, int> intMZ1cnt = new Dictionary<string, int>();
            Dictionary<string, double> intMZ1mu = new Dictionary<string, double>();
            Dictionary<string, double> intMZ1std = new Dictionary<string, double>();
            for (int i = fMS; i <= nMS; i++)
            {
                double time = rawFile.RetentionTimeFromScanNumber(i);
                string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(i));
                var scanStatistics = rawFile.GetScanStatsForScanNumber(i);
                var centroidStream = rawFile.GetCentroidStream(i, false);
                long maxInt = 0;
                int k = 0;
                if (title.Contains(" ms ") && scanStatistics.BasePeakIntensity>insThr)
                {
                    for (int j = 0; j < centroidStream.Length; j++)
                    {
                        long ic = (long)centroidStream.Intensities[j];
                        if (ic > insThr)
                        {
                            double MZ1val = centroidStream.Masses[j];
                            double ppmValA = MZ1val * (1 + ppmTol * 1e-6);
                            double ppmValB = MZ1val * (1 - ppmTol * 1e-6);
                            MZ1val = Math.Round(MZ1val, errTol, MidpointRounding.AwayFromZero);
                            ppmValA = Math.Round(ppmValA, errTol, MidpointRounding.AwayFromZero);
                            ppmValB = Math.Round(ppmValB, errTol, MidpointRounding.AwayFromZero);

                            string MZ1R = MZ1val.ToString();
                            string MZ1RA = ppmValA.ToString();
                            string MZ1RB = ppmValB.ToString();
                            tic += ic;
                            tms++;
                            if (intMZ1.ContainsKey(MZ1R))
                            {
                                double diffMZ1val = centroidStream.Masses[j] - MZ1val;
                                intMZ1[MZ1R] += ic; intMZ1cnt[MZ1R]++; intMZ1mu[MZ1R] += diffMZ1val; intMZ1std[MZ1R] += (diffMZ1val * diffMZ1val); }
                            else { intMZ1.Add(MZ1R, ic); intMZ1cnt.Add(MZ1R, 1); intMZ1mu.Add(MZ1R, 0); intMZ1std.Add(MZ1R, 0); }
                            if (intMZ1.ContainsKey(MZ1RA)) {
                                double diffMZ1val = centroidStream.Masses[j] - ppmValA;
                                intMZ1[MZ1RA] += ic; intMZ1cnt[MZ1RA]++; intMZ1mu[MZ1RA] += diffMZ1val; intMZ1std[MZ1RA] += (diffMZ1val * diffMZ1val); }
                            else { intMZ1.Add(MZ1RA, ic); intMZ1cnt.Add(MZ1RA, 1); intMZ1mu.Add(MZ1RA, 0); intMZ1std.Add(MZ1RA, 0); }
                            if (intMZ1.ContainsKey(MZ1RB)) {
                                double diffMZ1val = centroidStream.Masses[j] - ppmValB;
                                intMZ1[MZ1RB] += ic; intMZ1cnt[MZ1RB]++; intMZ1mu[MZ1RB] += diffMZ1val; intMZ1std[MZ1RB] += (diffMZ1val * diffMZ1val); }
                            else { intMZ1.Add(MZ1RB, ic); intMZ1cnt.Add(MZ1RB, 1); intMZ1mu.Add(MZ1RB, 0); intMZ1std.Add(MZ1RB, 0); }
                            if (ic >= maxInt) { maxInt = ic; maxIntSum += maxInt; k = j; }
                        }
                        //                      writeMS1.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}", tms, time, i, j, centroidStream.Masses[j], centroidStream.Intensities[j], scanStatistics.BasePeakMass, maxInt, tic, maxIntSum);
                    }
                    if (i%100==0) { Console.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}", i, centroidStream.Length, scanStatistics.BasePeakMass, maxInt, time, centroidStream.Masses[k], maxIntSum, tms); }
                    writeMS1.WriteLine("{0},{1},{2},{3},{4},{5},{6},{7}", i, centroidStream.Length, scanStatistics.BasePeakMass, maxInt, time, centroidStream.Masses[k], maxIntSum, tms);
                    //                    samples[i-1] = new Complex(scanStatistics.BasePeakMass,time);
                    //                if(i>1&&Math.Ceiling(samples[i-1].Real)<samples[i-2].Real){Console.WriteLine("{0}\t{1}\t{2}\t{3}", i, scanStatistics.BasePeakMass, maxIntSum,samples[i-2].Real-samples[i-1].Real);}
                }
            }
            /*            Fourier.Forward(samples, FourierOptions.NoScaling);
                        StreamWriter writeFFT = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".FFT.txt");
                        writeFFT.WriteLine("MZ\tRT\tangle\tmagnitude");
                        for (int i = fMS-1; i < nMS; i++)
                        {
                            double magnitude = (2.0 / nMS) * (Math.Abs(Math.Sqrt(Math.Pow(samples[i].Real, 2) + Math.Pow(samples[i].Imaginary, 2))));
                            double angle = Math.Atan(samples[i].Imaginary / samples[i].Real);
                            writeFFT.WriteLine("{0}\t{1}\t{2}\t{3}", samples[i].Real, samples[i].Imaginary, angle, magnitude);
                        }
            writeFFT.Close();
            */
            Console.WriteLine("#TIC>={0}intensity:\t{1}\t{2}", insThr, tms, tic);
            writeMS1.Close();
            // Stats https://rosettacode.org/wiki/Statistics/Basic#C.23 
            const int numBuckets = 10;
            long sampleSize = tms;
            var histogram = new Histogram(intMZ1cnt.Keys.ToList().Select(x => Double.Parse(x)).ToList(), numBuckets);
            Console.WriteLine("Sample size: {0:N0}", sampleSize);
            for (int i = 0; i < numBuckets; i++)
            {
                string bar = new String('#', (int)(histogram[i].Count * 360 / sampleSize));
                Console.WriteLine(" {0:0.00} : {1}", histogram[i].LowerBound, bar);
            }
            var statistics = new DescriptiveStatistics(intMZ1.Values.ToList().Select(x => (double)x).ToList());
            Console.WriteLine("  Mean: " + statistics.Mean);
            Console.WriteLine("StdDev: " + statistics.StandardDeviation);
            StreamWriter writeMZ1R = new StreamWriter(rawFile.FileName + ".intensityThreshold" + insThr + ".PPM" + ppmTol + ".errTolDecimalPlace" + errTol + ".Time" + timeStart+ ".MZ1R.csv");
            writeMZ1R.WriteLine("MZ1,sumIntensity,Deviation, RMSD,CV~,PPM~,log2sumIntensity,ionsWithinErrTolerance");
            foreach (var element in intMZ1.OrderByDescending(x => x.Value)) {
                if (intMZ1cnt[element.Key]>1) {
                    double mz1 = Convert.ToDouble(element.Key);
                    double deviation = intMZ1mu[element.Key] / intMZ1cnt[element.Key];
                    double rmsd = Math.Sqrt(intMZ1std[element.Key] / intMZ1cnt[element.Key]);
                    writeMZ1R.WriteLine("{0},{1},{2},{3},{4},{5},{6},{7}", mz1, element.Value, deviation, rmsd, 100*rmsd/(deviation + mz1), 1000000 * Math.Abs(deviation)/mz1, Math.Log(element.Value, 2), intMZ1cnt[element.Key]);
                 }
            }
            writeMZ1R.Close();
            rawFile.Dispose();
            rawFile.Dispose();
        }
    }
}
