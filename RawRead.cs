//released under GPL version 2 or later: sharma.animesh@gmail.com
//install mono and compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll /reference:CometWrapper.dll
//run: mono RawRead.exe <ThermoOrbitrapRawfileName> <FastaIndex> <intensityThreshold>(optional) <chargeThreshold>(optional)
//windows with dotnet: c:\Windows\Microsoft.NET\Framework\v4.0.30319\msbuild RawRead.csproj 
namespace RawRead
{
    using ThermoFisher.CommonCore.RawFileReader;//RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader
    using ThermoFisher.CommonCore.Data.Business;//RawFileReader
    using ThermoFisher.CommonCore.MassPrecisionEstimator;//RawFileReader
    using MathNet.Numerics.IntegralTransforms;//nuget or dotnet https://www.nuget.org/packages/MathNet.Numerics/
    using System.Numerics;//https://www.nuget.org/packages/System.Runtime.Numerics/
    using System;
    using System.IO;
    using System.Collections.Generic;
    using ThermoFisher.CommonCore.Data.Interfaces;
    using CometWrapper;//compiled from https://sourceforge.net/code-snapshots/svn/c/co/comet-ms/code/comet-ms-code-r1296-trunk-comet-ms.zip

    internal class RawRead2PeakList
    {
        
        static void Main(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[0]) || !File.Exists(args[1])) { Console.WriteLine("USAGE: {0} fileNameRaw fileNameFastaIndex intensityThreshold(optional) chargeThreshold(optional)", AppDomain.CurrentDomain.FriendlyName); return; }
            var rawFile = RawFileReaderAdapter.FileFactory(args[0]);
            if(!rawFile.IsOpen) { Console.WriteLine("Raw file {1} is already Open, probably not finish writing", rawFile.FileError, args[0]); return; }
            if(rawFile.IsError) { Console.WriteLine("Error opening {1}, probably not proper orbitrap raw file? Tested only on Elite, QE and HF...", rawFile.FileError, args[0]); return; }
            CometSearchManagerWrapper SearchMgr = new CometSearchManagerWrapper();
            SearchSettings searchParams = new SearchSettings();
            string sDB = args[1];
            double dPeptideMassLow = 0;
            double dPeptideMassHigh = 0;
            searchParams.ConfigureInputSettings(SearchMgr, ref dPeptideMassLow, ref dPeptideMassHigh, ref sDB);
            double insThr = 0;
            int chgThr = 0;
            if (args.Length == 3) { insThr = double.Parse(args[2]); }
            if (args.Length == 4) { insThr = double.Parse(args[2]); chgThr = int.Parse(args[3]); }
            rawFile.SelectInstrument(Device.MS, 1);
            int fMS = rawFile.RunHeaderEx.FirstSpectrum;
            int nMS = rawFile.RunHeaderEx.LastSpectrum;
            var fFilter = rawFile.GetFilterForScanNumber(fMS);
            var nFilter = rawFile.GetFilterForScanNumber(nMS);
            //GetSpectrum(rawFile, fMS, fFilter.ToString(), false);
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName + "\n" + "#prescan(s):\t" + nMS + "\n" + "#RT length:\t" + (eTime - sTime) + "\n" + "#version:\t" + rawFile.FileHeader.Revision + "\n" + "#create date:\t" + rawFile.FileHeader.CreationDate + "\n" + "#machine:\t" + rawFile.FileHeader.WhoCreatedId + "\n" + "#serial:\t" + rawFile.GetInstrumentData().SerialNumber + "\n" + "#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion + "\n" + "#resolution:\t" + rawFile.RunHeaderEx.MassResolution + "\n" + "#massrange:\t" + rawFile.RunHeaderEx.LowMass + "-" + rawFile.RunHeaderEx.HighMass + "\n" + "#sample:\t" + rawFile.SampleInformation.Vial + "\n" + "#volume:\t" + rawFile.SampleInformation.SampleVolume + "\n" + "#injection:\t" + rawFile.SampleInformation.InjectionVolume + "\n" + "#dilution:\t" + rawFile.SampleInformation.DilutionFactor + "\n" + "#filter:\t" + fFilter.ToString() + "\n" + "#filterN:\t" + nFilter.ToString());
            //foreach (var device in rawFile.GetAllInstrumentNamesFromInstrumentMethod()) { Console.WriteLine("#method:\t" + device);}//windows only
            StreamWriter writeMZ = new StreamWriter(rawFile.FileName + ".MZ.txt");
            var scan = Scan.FromFile(rawFile, fMS);
            var scanEvent = rawFile.GetScanEventForScanNumber(fMS);
            LogEntry logEntry = rawFile.GetTrailerExtraInformation(fMS);
            var trailerHeadings = new List<string>();
            var trailerValues = new List<string>();
            for (var i = 0; i < logEntry.Length; i++) {trailerHeadings.Add(logEntry.Labels[i]);trailerValues.Add(logEntry.Values[i]);}
            IPrecisionEstimate precisionEstimate = new PrecisionEstimate();
            var ionTime = precisionEstimate.GetIonTime(scanEvent.MassAnalyzer, scan, trailerHeadings, trailerValues);
            var listResults = precisionEstimate.GetMassPrecisionEstimate(scan, scanEvent.MassAnalyzer, ionTime, rawFile.RunHeader.MassResolution);
            if (listResults.Count > 0)
            {
                writeMZ.WriteLine("Mass\tmmu\tppm\t");
                foreach (var result in listResults){writeMZ.WriteLine("{0:F5}\t{1:F3}\t{2:F2}",result.Mass, result.MassAccuracyInMmu, result.MassAccuracyInPpm);}
            }
            // Get the chromatogram from the RAW file.
            ChromatogramTraceSettings settings = new ChromatogramTraceSettings(TraceType.BasePeak);
            var data = rawFile.GetChromatogramData(new IChromatogramSettings[] { settings }, fMS, nMS);
            var trace = ChromatogramSignal.FromChromatogramData(data);
            StreamWriter writeChromatogram = new StreamWriter(rawFile.FileName + ".chromatogram.txt");
            if (trace[0].Length > 0)
            {
                writeChromatogram.WriteLine("BasePeak({0}points)\tRT\tIntensity", trace[0].Length);
                for (int i = 0; i < trace[0].Length; i++){writeChromatogram.WriteLine("{0}\t{1:F3}\t{2:F0}", i, 60*trace[0].Times[i], trace[0].Intensities[i]);}
            }
            int tms = 0;
            double tic = 0;
            double maxIntSum = 0;
            Complex[] samples = new Complex[nMS];
            StreamWriter writeMS1 = new StreamWriter(rawFile.FileName + ".profile.intensity" + insThr + ".charge" + chgThr + "." + "MS.txt");
            StreamWriter writeMS2 = new StreamWriter(rawFile.FileName + ".centroid." + "MGF");
            StreamWriter writeMS2p = new StreamWriter(rawFile.FileName + ".profile." + "MGF");
            for (int i = fMS; i <= nMS; i++)
            {
                double time = rawFile.RetentionTimeFromScanNumber(i);
                string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(i));
                var scanStatistics = rawFile.GetScanStatsForScanNumber(i);
                var segmentedScan = rawFile.GetSegmentedScanFromScanNumber(i, scanStatistics);
                var centroidStream = rawFile.GetCentroidStream(i, false);
                double maxMass = 0;
                double maxInt = 0;
                string charge = "";//centroidStream.Charges[i].ToString();
                logEntry = rawFile.GetTrailerExtraInformation(i);
                for (var l = 0; l < logEntry.Length; l++) {
                  if(logEntry.Labels[l]=="Charge State:"){charge=logEntry.Values[l];}
                  //Console.WriteLine("{0}-{1}-{2}",l,logEntry.Labels[l],logEntry.Values[l]);
                }
                if (scanStatistics.IsCentroidScan)
                {
                    writeMS2.WriteLine("BEGIN IONS\nTITLE={0}\t{3}\tSCANS={2}\nRTINSECONDS={1}\nPEPMASS={6}\t{4}\t{5}\nCHARGE={7}+", i, time * 60, title, segmentedScan.Positions.Length, scanStatistics.BasePeakMass, scanStatistics.BasePeakIntensity, rawFile.GetScanEventForScanNumber(i).GetReaction(0).PrecursorMass,charge);
                    for (int j = 0; j < segmentedScan.Positions.Length; j++) { writeMS2.WriteLine("{0} {1}", segmentedScan.Positions[j], segmentedScan.Intensities[j]); }
                    writeMS2.WriteLine("END IONS\n");
                }
                else //profile?
                {
                  if (!title.Contains(" ms "))
                  {
                    writeMS2p.WriteLine("BEGIN IONS\nTITLE={0}\t{1}\tSCANS={2}\nRTINSECONDS={3}\nPEPMASS={4}\nCHARGE={5}+", i, title, centroidStream.Length, time * 60,  rawFile.GetScanEventForScanNumber(i).GetReaction(0).PrecursorMass,charge);
                    for (int j = 0; j < centroidStream.Length; j++) { writeMS2p.WriteLine("{0} {1}", centroidStream.Masses[j], centroidStream.Intensities[j]); }
                    writeMS2p.WriteLine("END IONS\n");
                  }
                  else //profile?
                  {
                    writeMS1.WriteLine("Scan{0}\tMZ\tcharge\tintensity\t{1}\t{2}", i, title, centroidStream.Length);
                    for (int j = 0; j < centroidStream.Length; j++)
                    {
                        if (centroidStream.Charges[j] >= chgThr && centroidStream.Intensities[j] >= insThr)
                        {
                            writeMS1.WriteLine("{0}\t{1}\t{3}\t{2}\t{4}", j, centroidStream.Masses[j], centroidStream.Intensities[j], centroidStream.Charges[j], centroidStream.Masses[j] * centroidStream.Charges[j] - centroidStream.Charges[j]);
                            tic += centroidStream.Intensities[j];
                            tms++;
                        }
                        if (centroidStream.Intensities[j] >= maxInt) { maxInt = centroidStream.Intensities[j]; maxMass = centroidStream.Masses[j]; maxIntSum += maxInt; }
                    }
                  }
                }
                Console.WriteLine("{0}\t{8}\t{7}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}", i, maxIntSum, title, maxMass, time, maxInt, charge, scanStatistics.TIC, scanStatistics.BasePeakMass);
                samples[i-1] = new Complex(maxMass,maxInt);
            }
            Fourier.Forward(samples, FourierOptions.NoScaling);
            StreamWriter writeFFT = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".charge" + chgThr + "." + "FFT.txt");
            writeFFT.WriteLine("mass\tintensity\tangle\tmagnitude");
            for (int i = fMS-1; i < nMS; i++)
            {
                double magnitude = (2.0 / nMS) * (Math.Abs(Math.Sqrt(Math.Pow(samples[i].Real, 2) + Math.Pow(samples[i].Imaginary, 2))));
                double angle = Math.Atan(samples[i].Imaginary / samples[i].Real);
                writeFFT.WriteLine("{0}\t{1}\t{2}\t{3}", samples[i].Real, samples[i].Imaginary, angle, magnitude);
            }
            Console.WriteLine("#TIC>={0}intensity:\t{1}", insThr, tic);
            Console.WriteLine("#Ions>=charge{0}:\t{1}", chgThr, tms);
            rawFile.Dispose();
            writeMS1.Close();
            writeMS2.Close();
            writeMZ.Close();
            writeFFT.Close();
            writeChromatogram.Close();
        }
        //shamelessly copied from comet-ms-code-r1296-trunk-comet-ms\comet-ms-code-r1296-trunk-comet-ms\RealtimeSearch\Search.cs
        class SearchSettings
        {
            public bool ConfigureInputSettings(CometSearchManagerWrapper SearchMgr,
               ref double dPeptideMassLow,
               ref double dPeptideMassHigh,
               ref string sDB)
            {
                String sTmp;
                int iTmp;
                double dTmp;

                SearchMgr.SetParam("database_name", sDB, sDB);

                dTmp = 20.0; //ppm window
                sTmp = dTmp.ToString();
                SearchMgr.SetParam("peptide_mass_tolerance", sTmp, dTmp);

                iTmp = 2; // 0=Da, 2=ppm
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("peptide_mass_units", sTmp, iTmp);

                iTmp = 30; // search time cutoff in milliseconds
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("max_index_runtime", sTmp, iTmp);

                iTmp = 1; // m/z tolerance
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("precursor_tolerance_type", sTmp, iTmp);

                iTmp = 2; // 0=off, 1=0/1 (C13 error), 2=0/1/2, 3=0/1/2/3, 4=-8/-4/0/4/8 (for +4/+8 labeling)
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("isotope_error", sTmp, iTmp);

                dTmp = 1.0005; // fragment bin width
                sTmp = dTmp.ToString();
                SearchMgr.SetParam("fragment_bin_tol", sTmp, dTmp);

                dTmp = 0.4; // fragment bin offset
                sTmp = dTmp.ToString();
                SearchMgr.SetParam("fragment_bin_offset", sTmp, dTmp);

                iTmp = 1; // 0=use flanking peaks, 1=M peak only
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("theoretical_fragment_ions", sTmp, iTmp);

                iTmp = 3; // maximum fragment charge
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("max_fragment_charge", sTmp, iTmp);

                iTmp = 6; // maximum precursor charge
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("max_precursor_charge", sTmp, iTmp);

                iTmp = 0; // 0=I and L are different, 1=I and L are same
                sTmp = iTmp.ToString();
                SearchMgr.SetParam("equal_I_and_L", sTmp, iTmp);

                // Now actually open the .idx database to read mass range from it
                int iLineCount = 0;
                bool bFoundMassRange = false;
                string strLine;
                System.IO.StreamReader dbFile = new System.IO.StreamReader(@sDB);

                while ((strLine = dbFile.ReadLine()) != null)
                {
                    string[] strParsed = strLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (strParsed[0].Equals("MassRange:"))
                    {
                        dPeptideMassLow = double.Parse(strParsed[1]);
                        dPeptideMassHigh = double.Parse(strParsed[2]);

                        var digestMassRange = new DoubleRangeWrapper(dPeptideMassLow, dPeptideMassHigh);
                        string digestMassRangeString = dPeptideMassLow.ToString() + " " + dPeptideMassHigh.ToString();
                        SearchMgr.SetParam("digest_mass_range", digestMassRangeString, digestMassRange);

                        bFoundMassRange = true;
                    }
                    iLineCount++;

                    if (iLineCount > 6)  // header information should only be in first few lines
                        break;
                }
                dbFile.Close();

                if (!bFoundMassRange)
                {
                    Console.WriteLine(" Error with indexed database format; missing MassRange header.\n");
                    System.Environment.Exit(1);
                }

                return true;
            }
        }
    }
}
