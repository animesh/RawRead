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
    using System.Xml.Linq;

    internal class RawRead2PeakList
    {
        static void Main(string[] args)
        {
            var rawFile = RawFileReaderAdapter.FileFactory("171010_Ip_Hela_ugi.raw");
            //var rawFile = RawFileReaderAdapter.FileFactory("20150512_BSA_The-PEG-envelope.raw");
            //var rawFile = RawFileReaderAdapter.FileFactory("20200219_KKL_SARS_CoV2_pool1_F1.raw");
            string timeStart = DateTime.Now.ToString("yyyyMMddHHmmss");
            if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("\nUSAGE: {0} *FILENAME* Threshold4intensity(default 0) Threshold4errorTolerance(default 3 for precision upto 3 decimal points) hmdbFile(default is hmdb_metabolites.sample.xml)\n\n :using default FILENAME:171010_Ip_Hela_ugi.raw and unzipped https://hmdb.ca/system/downloads/current/hmdb_metabolites.zip presenting Metabolite and Protein Data in XML format\n", AppDomain.CurrentDomain.FriendlyName);  }
            if(args.Length > 0){ rawFile = RawFileReaderAdapter.FileFactory(args[0]);}
            if(!rawFile.IsOpen) { Console.WriteLine("Raw file {1} is already Open, probably not finish writing", rawFile.FileError, args[0]); return; }
            if(rawFile.IsError) { Console.WriteLine("Error opening {1}, probably not proper orbitrap raw file? This program is tested only on Elite, QE and HF orbitrap raw files...", rawFile.FileError, args[0]); return; }
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
            long tms = 0;
            long tic = 0;
            long maxIntSum = 0;
            //            Complex[] samples = new Complex[nMS];
            StreamWriter writeMS1 = new StreamWriter(fileMS1);
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
                            double MZ1val = Math.Round((Double)centroidStream.Masses[j], errTol, MidpointRounding.AwayFromZero);
                            string MZ1R = MZ1val.ToString();
                            double diffMZ1val = centroidStream.Masses[j] - MZ1val;
                            tic += ic;
                            tms++;
                            if (intMZ1.ContainsKey(MZ1R)) { intMZ1[MZ1R] += ic; intMZ1cnt[MZ1R]++; intMZ1mu[MZ1R] += diffMZ1val; intMZ1std[MZ1R] += (diffMZ1val* diffMZ1val); }
                            else { intMZ1.Add(MZ1R, ic); intMZ1cnt.Add(MZ1R, 1); intMZ1mu.Add(MZ1R, 0); intMZ1std.Add(MZ1R, 0); }
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
            var histogram = new Histogram(intMZ1.Keys.ToList().Select(x => Double.Parse(x)).ToList(), numBuckets);
            Console.WriteLine("Sample size: {0:N0}", sampleSize);
            for (int i = 0; i < numBuckets; i++)
            {
                string bar = new String('#', (int)(histogram[i].Count * 360 / sampleSize));
                Console.WriteLine(" {0:0.00} : {1}", histogram[i].LowerBound, bar);
            }
            var statistics = new DescriptiveStatistics(intMZ1.Values.ToList().Select(x => (double)x).ToList());
            Console.WriteLine("  Mean: " + statistics.Mean);
            Console.WriteLine("StdDev: " + statistics.StandardDeviation);
            StreamWriter writeMZ1R = new StreamWriter(fileMZ1R);
            writeMZ1R.WriteLine("MZ1,sumIntensity,Mean, Deviation,log2sumIntensity,ionsWithinErrTolerance");
            foreach (var element in intMZ1.OrderByDescending(x => x.Value)) { writeMZ1R.WriteLine("{0},{1},{2},{3},{4},{5}", element.Key, element.Value, intMZ1mu[element.Key], intMZ1std[element.Key], Math.Log(element.Value,2), intMZ1cnt[element.Key]); }
            writeMZ1R.Close();
            rawFile.Dispose();
            //Parse XML https://docs.microsoft.com/en-us/dotnet/standard/linq/linq-xml-overview
            //IEnumerable<XElement> metaboliteOrder = XElement.Load(hmdbFile).Descendants();Elements("metabolite")
            foreach (XElement name in XElement.Load(hmdbFile).Descendants()) { if (!name.IsEmpty) { Console.WriteLine("name:{0}", name.Value); } }
            Console.WriteLine("\nResults written to files-\nMS scan info:\t{0}\nMZ1 statistics:\t{1}", fileMS1, fileMZ1R);
        }
    }
}
