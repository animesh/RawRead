//released under GPL version 2 or later: sharma.animesh@gmail.com
//install mono and compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
// compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll -out:RawRead.exe
// windows with dotnet: c:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
// run: mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
// mono RawRead.exe file.raw 100000 2

namespace RawRead
{
    using ThermoFisher.CommonCore.RawFileReader;
    using ThermoFisher.CommonCore.Data.Business;
    using ThermoFisher.CommonCore.MassPrecisionEstimator;
    using ThermoFisher.CommonCore.Data.Interfaces;
    using MathNet.Numerics.IntegralTransforms;
    using System.Numerics;
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Globalization;

    internal class RawRead2PeakListFixed
    {
        private const double Proton = 1.007276466812;

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("USAGE: {0} fileName intensityThreshold(optional) chargeThreshold(optional)", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];
            double intensityThreshold = 0.0;
            int chargeThreshold = 0;

            if (args.Length >= 2)
            {
                double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out intensityThreshold);
            }

            if (args.Length >= 3)
            {
                int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out chargeThreshold);
            }

            var rawFile = RawFileReaderAdapter.FileFactory(rawPath);

            if (!rawFile.IsOpen)
            {
                Console.WriteLine("Raw file {0} is not open. FileError: {1}", rawPath, rawFile.FileError);
                return;
            }

            if (rawFile.IsError)
            {
                Console.WriteLine("Error opening {0}. FileError: {1}", rawPath, rawFile.FileError);
                return;
            }

            rawFile.SelectInstrument(Device.MS, 1);

            int firstScan = rawFile.RunHeaderEx.FirstSpectrum;
            int lastScan = rawFile.RunHeaderEx.LastSpectrum;
            int scanCount = lastScan - firstScan + 1;

            var firstFilter = rawFile.GetFilterForScanNumber(firstScan);
            var lastFilter = rawFile.GetFilterForScanNumber(lastScan);

            double startTime = rawFile.RunHeaderEx.StartTime;
            double endTime = rawFile.RunHeaderEx.EndTime;

            Console.WriteLine("#filename:\t" + rawFile.FileName);
            Console.WriteLine("#prescan(s):\t" + scanCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#RT length:\t" + (endTime - startTime).ToString("G17", CultureInfo.InvariantCulture));
            Console.WriteLine("#version:\t" + rawFile.FileHeader.Revision);
            Console.WriteLine("#create date:\t" + rawFile.FileHeader.CreationDate);
            Console.WriteLine("#machine:\t" + rawFile.FileHeader.WhoCreatedId);
            Console.WriteLine("#serial:\t" + rawFile.GetInstrumentData().SerialNumber);
            Console.WriteLine("#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion);
            Console.WriteLine("#resolution:\t" + rawFile.RunHeaderEx.MassResolution.ToString("G17", CultureInfo.InvariantCulture));
            Console.WriteLine("#massrange:\t" + rawFile.RunHeaderEx.LowMass.ToString("G17", CultureInfo.InvariantCulture) + "-" + rawFile.RunHeaderEx.HighMass.ToString("G17", CultureInfo.InvariantCulture));
            Console.WriteLine("#sample:\t" + rawFile.SampleInformation.Vial);
            Console.WriteLine("#volume:\t" + rawFile.SampleInformation.SampleVolume);
            Console.WriteLine("#injection:\t" + rawFile.SampleInformation.InjectionVolume);
            Console.WriteLine("#dilution:\t" + rawFile.SampleInformation.DilutionFactor);
            Console.WriteLine("#filter:\t" + firstFilter.ToString());
            Console.WriteLine("#filterN:\t" + lastFilter.ToString());

            WriteMassPrecision(rawFile, firstScan);
            WriteBasePeakChromatogram(rawFile, firstScan, lastScan);

            double tic = 0.0;
            int ionCount = 0;
            double maxIntSum = 0.0;
            Complex[] samples = new Complex[scanCount];

            using (var writeMS1 = new StreamWriter(rawFile.FileName + ".profile.intensity" + intensityThreshold.ToString(CultureInfo.InvariantCulture) + ".charge" + chargeThreshold.ToString(CultureInfo.InvariantCulture) + ".MS.txt"))
            using (var writeMS2 = new StreamWriter(rawFile.FileName + ".centroid.MGF"))
            using (var writeMS2Profile = new StreamWriter(rawFile.FileName + ".profile.MGF"))
            {
                for (int scanNumber = firstScan; scanNumber <= lastScan; scanNumber++)
                {
                    double time = rawFile.RetentionTimeFromScanNumber(scanNumber);
                    string title = SafeOneLine(string.Join(" ", rawFile.GetScanEventForScanNumber(scanNumber)));
                    var scanStatistics = rawFile.GetScanStatsForScanNumber(scanNumber);

                    var segmentedScan = rawFile.GetSegmentedScanFromScanNumber(scanNumber, scanStatistics);
                    var centroidStream = rawFile.GetCentroidStream(scanNumber, false);

                    double maxMass = 0.0;
                    double maxInt = 0.0;
                    string charge = GetTrailerValue(rawFile, scanNumber, "Charge State:");

                    if (scanStatistics.IsCentroidScan)
                    {
                        double precursorMass = SafePrecursorMass(rawFile, scanNumber);

                        writeMS2.WriteLine("BEGIN IONS");
                        writeMS2.WriteLine("TITLE={0}\tpeaks={1}\tSCANS={2}", scanNumber, segmentedScan.Positions.Length, title);
                        writeMS2.WriteLine("RTINSECONDS={0}", (time * 60.0).ToString("G17", CultureInfo.InvariantCulture));
                        writeMS2.WriteLine("PEPMASS={0}\t{1}\t{2}", precursorMass.ToString("G17", CultureInfo.InvariantCulture), scanStatistics.BasePeakMass.ToString("G17", CultureInfo.InvariantCulture), scanStatistics.BasePeakIntensity.ToString("G17", CultureInfo.InvariantCulture));
                        if (!string.IsNullOrWhiteSpace(charge))
                        {
                            writeMS2.WriteLine("CHARGE={0}+", charge);
                        }

                        for (int j = 0; j < segmentedScan.Positions.Length; j++)
                        {
                            writeMS2.WriteLine(segmentedScan.Positions[j].ToString("G17", CultureInfo.InvariantCulture) + " " + segmentedScan.Intensities[j].ToString("G17", CultureInfo.InvariantCulture));
                        }

                        writeMS2.WriteLine("END IONS");
                        writeMS2.WriteLine();
                    }
                    else
                    {
                        if (!title.Contains(" ms "))
                        {
                            double precursorMass = SafePrecursorMass(rawFile, scanNumber);

                            writeMS2Profile.WriteLine("BEGIN IONS");
                            writeMS2Profile.WriteLine("TITLE={0}\t{1}\tSCANS={2}", scanNumber, title, centroidStream.Length);
                            writeMS2Profile.WriteLine("RTINSECONDS={0}", (time * 60.0).ToString("G17", CultureInfo.InvariantCulture));
                            writeMS2Profile.WriteLine("PEPMASS={0}", precursorMass.ToString("G17", CultureInfo.InvariantCulture));
                            if (!string.IsNullOrWhiteSpace(charge))
                            {
                                writeMS2Profile.WriteLine("CHARGE={0}+", charge);
                            }

                            for (int j = 0; j < centroidStream.Length; j++)
                            {
                                writeMS2Profile.WriteLine(centroidStream.Masses[j].ToString("G17", CultureInfo.InvariantCulture) + " " + centroidStream.Intensities[j].ToString("G17", CultureInfo.InvariantCulture));
                            }

                            writeMS2Profile.WriteLine("END IONS");
                            writeMS2Profile.WriteLine();
                        }
                        else
                        {
                            writeMS1.WriteLine("Scan{0}\tMZ\tcharge\tintensity\tneutralMass\t{1}\t{2}", scanNumber, title, centroidStream.Length);

                            for (int j = 0; j < centroidStream.Length; j++)
                            {
                                double mz = centroidStream.Masses[j];
                                double intensity = centroidStream.Intensities[j];
                                double chargeValue = centroidStream.Charges[j];
                                int z = (int)Math.Round(chargeValue);

                                if (intensity >= maxInt)
                                {
                                    maxInt = intensity;
                                    maxMass = mz;
                                }

                                if (z >= chargeThreshold && intensity >= intensityThreshold)
                                {
                                    double neutralMass = double.NaN;

                                    if (z > 0)
                                    {
                                        neutralMass = mz * z - z * Proton;
                                    }

                                    writeMS1.WriteLine(
                                        j.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        mz.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                                        z.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        intensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                                        neutralMass.ToString("G17", CultureInfo.InvariantCulture)
                                    );

                                    tic += intensity;
                                    ionCount++;
                                }
                            }
                        }
                    }

                    maxIntSum += maxInt;

                    Console.WriteLine(
                        scanNumber.ToString(CultureInfo.InvariantCulture) + "\t" +
                        scanStatistics.BasePeakMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        scanStatistics.TIC.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        maxIntSum.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        title + "\t" +
                        maxMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        time.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        maxInt.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        charge
                    );

                    int sampleIndex = scanNumber - firstScan;

                    if (sampleIndex >= 0 && sampleIndex < samples.Length)
                    {
                        samples[sampleIndex] = new Complex(maxMass, maxInt);
                    }
                }
            }

            WriteFft(rawFile.FileName + ".intensity" + intensityThreshold.ToString(CultureInfo.InvariantCulture) + ".charge" + chargeThreshold.ToString(CultureInfo.InvariantCulture) + ".FFT.txt", samples);

            Console.WriteLine("#TIC>={0} intensity:\t{1}", intensityThreshold.ToString(CultureInfo.InvariantCulture), tic.ToString("G17", CultureInfo.InvariantCulture));
            Console.WriteLine("#Ions>=charge{0}:\t{1}", chargeThreshold.ToString(CultureInfo.InvariantCulture), ionCount.ToString(CultureInfo.InvariantCulture));

            rawFile.Dispose();
        }

        private static void WriteMassPrecision(dynamic rawFile, int scanNumber)
        {
            try
            {
                using (var writer = new StreamWriter(rawFile.FileName + ".MZ.txt"))
                {
                    var scan = Scan.FromFile(rawFile, scanNumber);
                    var scanEvent = rawFile.GetScanEventForScanNumber(scanNumber);
                    LogEntry logEntry = rawFile.GetTrailerExtraInformation(scanNumber);

                    var trailerHeadings = new List<string>();
                    var trailerValues = new List<string>();

                    for (int i = 0; i < logEntry.Length; i++)
                    {
                        trailerHeadings.Add(logEntry.Labels[i]);
                        trailerValues.Add(logEntry.Values[i]);
                    }

                    IPrecisionEstimate precisionEstimate = new PrecisionEstimate();
                    double ionTime = precisionEstimate.GetIonTime(scanEvent.MassAnalyzer, scan, trailerHeadings, trailerValues);
                    var results = precisionEstimate.GetMassPrecisionEstimate(scan, scanEvent.MassAnalyzer, ionTime, rawFile.RunHeader.MassResolution);

                    if (results.Count > 0)
                    {
                        writer.WriteLine("Mass\tmmu\tppm");

                        foreach (var result in results)
                        {
                            writer.WriteLine(
                                result.Mass.ToString("F5", CultureInfo.InvariantCulture) + "\t" +
                                result.MassAccuracyInMmu.ToString("F3", CultureInfo.InvariantCulture) + "\t" +
                                result.MassAccuracyInPpm.ToString("F2", CultureInfo.InvariantCulture)
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Warning: mass precision output failed: {0}", ex.Message);
            }
        }

        private static void WriteBasePeakChromatogram(dynamic rawFile, int firstScan, int lastScan)
        {
            try
            {
                ChromatogramTraceSettings settings = new ChromatogramTraceSettings(TraceType.BasePeak);
                var data = rawFile.GetChromatogramData(new IChromatogramSettings[] { settings }, firstScan, lastScan);
                var trace = ChromatogramSignal.FromChromatogramData(data);

                using (var writer = new StreamWriter(rawFile.FileName + ".chromatogram.txt"))
                {
                    if (trace[0].Length > 0)
                    {
                        writer.WriteLine("BasePeak({0}points)\tRT\tIntensity", trace[0].Length);

                        for (int i = 0; i < trace[0].Length; i++)
                        {
                            writer.WriteLine(
                                i.ToString(CultureInfo.InvariantCulture) + "\t" +
                                (60.0 * trace[0].Times[i]).ToString("F3", CultureInfo.InvariantCulture) + "\t" +
                                trace[0].Intensities[i].ToString("F0", CultureInfo.InvariantCulture)
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Warning: chromatogram output failed: {0}", ex.Message);
            }
        }

        private static void WriteFft(string path, Complex[] samples)
        {
            try
            {
                Fourier.Forward(samples, FourierOptions.NoScaling);

                using (var writer = new StreamWriter(path))
                {
                    writer.WriteLine("mass\tintensity\tangle\tmagnitude");

                    for (int i = 0; i < samples.Length; i++)
                    {
                        double magnitude = (2.0 / samples.Length) * Math.Sqrt(samples[i].Real * samples[i].Real + samples[i].Imaginary * samples[i].Imaginary);
                        double angle = samples[i].Real != 0.0 ? Math.Atan(samples[i].Imaginary / samples[i].Real) : 0.0;

                        writer.WriteLine(
                            samples[i].Real.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                            samples[i].Imaginary.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                            angle.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                            magnitude.ToString("G17", CultureInfo.InvariantCulture)
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Warning: FFT output failed: {0}", ex.Message);
            }
        }

        private static string GetTrailerValue(dynamic rawFile, int scanNumber, string label)
        {
            try
            {
                var logEntry = rawFile.GetTrailerExtraInformation(scanNumber);

                for (int i = 0; i < logEntry.Length; i++)
                {
                    if (logEntry.Labels[i] == label)
                    {
                        return logEntry.Values[i];
                    }
                }
            }
            catch
            {
            }

            return "";
        }

        private static double SafePrecursorMass(dynamic rawFile, int scanNumber)
        {
            try
            {
                return rawFile.GetScanEventForScanNumber(scanNumber).GetReaction(0).PrecursorMass;
            }
            catch
            {
                return double.NaN;
            }
        }

        private static string SafeOneLine(string text)
        {
            if (text == null)
            {
                return "";
            }

            return text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        }
    }
}
