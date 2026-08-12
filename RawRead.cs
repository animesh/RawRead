// RawRead: Thermo RAW reader targeting .NET 8 / ThermoFisher CommonCore 8.0.37
// Build: dotnet build -c Release
// Run:   dotnet bin/Release/net8.0/RawRead.dll <rawfile> [intensityThreshold] [chargeThreshold]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.Data.Interfaces;
using ThermoFisher.CommonCore.MassPrecisionEstimator;
using ThermoFisher.CommonCore.RawFileReader;
namespace RawRead;
internal static class RawRead2PeakList {
    static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    static int I(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    static void Main(string[] args) {
        if (args.Length < 1 || !File.Exists(args[0])) {
            Console.WriteLine("USAGE: {0} fileName [intensityThreshold] [chargeThreshold]", AppDomain.CurrentDomain.FriendlyName);
            return;
        }
        double intensityThreshold = args.Length >= 2 ? D(args[1]) : 0;
        int chargeThreshold = args.Length >= 3 ? I(args[2]) : 0;
        var rawFile = RawFileReaderAdapter.FileFactory(args[0]);
        if (!rawFile.IsOpen || rawFile.IsError || rawFile.InAcquisition) {
            Console.WriteLine("Cannot open {0}: {1}", args[0], rawFile.FileError);
            rawFile.Dispose();
            return;
        }
        try {
            rawFile.SelectInstrument(Device.MS, 1);
            int firstScan = rawFile.RunHeaderEx.FirstSpectrum;
            int lastScan = rawFile.RunHeaderEx.LastSpectrum;
            if (lastScan < firstScan) { Console.WriteLine("Invalid scan range."); return; }
            Console.WriteLine(
                "#filename:\t{0}\n#prescan(s):\t{1}\n#RT length:\t{2}\n#version:\t{3}\n" +
                "#create date:\t{4}\n#machine:\t{5}\n#serial:\t{6}\n#writer:\t{7}\n" +
                "#resolution:\t{8}\n#massrange:\t{9}-{10}\n#sample:\t{11}\n" +
                "#volume:\t{12}\n#injection:\t{13}\n#dilution:\t{14}\n#filter:\t{15}\n#filterN:\t{16}",
                rawFile.FileName, lastScan, rawFile.RunHeaderEx.EndTime - rawFile.RunHeaderEx.StartTime,
                rawFile.FileHeader.Revision, rawFile.FileHeader.CreationDate, rawFile.FileHeader.WhoCreatedId,
                rawFile.GetInstrumentData().SerialNumber, rawFile.GetInstrumentData().SoftwareVersion,
                rawFile.RunHeaderEx.MassResolution, rawFile.RunHeaderEx.LowMass, rawFile.RunHeaderEx.HighMass,
                rawFile.SampleInformation.Vial, rawFile.SampleInformation.SampleVolume,
                rawFile.SampleInformation.InjectionVolume, rawFile.SampleInformation.DilutionFactor,
                rawFile.GetFilterForScanNumber(firstScan), rawFile.GetFilterForScanNumber(lastScan));
            // mass precision
            var firstScanData = Scan.FromFile(rawFile, firstScan);
            var firstScanEvent = rawFile.GetScanEventForScanNumber(firstScan);
            var logEntry = rawFile.GetTrailerExtraInformation(firstScan);
            var headings = new List<string>(); var vals = new List<string>();
            for (int i = 0; i < logEntry.Length; i++) { headings.Add(logEntry.Labels[i]); vals.Add(logEntry.Values[i]); }
            IPrecisionEstimate pe = new PrecisionEstimate();
            var ionTime = pe.GetIonTime(firstScanEvent.MassAnalyzer, firstScanData, headings, vals);
            var mpResults = pe.GetMassPrecisionEstimate(firstScanData, firstScanEvent.MassAnalyzer, ionTime, rawFile.RunHeader.MassResolution);
            using var writeMZ = new StreamWriter(rawFile.FileName + ".MZ.txt");
            if (mpResults.Count > 0) {
                writeMZ.WriteLine("Mass\tmmu\tppm\t");
                foreach (var r in mpResults)
                    writeMZ.WriteLine("{0:F5}\t{1:F3}\t{2:F2}", r.Mass, r.MassAccuracyInMmu, r.MassAccuracyInPpm);
            }
            // chromatogram
            var chromData = rawFile.GetChromatogramData(
                new IChromatogramSettings[] { new ChromatogramTraceSettings(TraceType.BasePeak) }, firstScan, lastScan);
            var trace = ChromatogramSignal.FromChromatogramData(chromData);
            using var writeChromatogram = new StreamWriter(rawFile.FileName + ".chromatogram.txt");
            if (trace.Length > 0 && trace[0].Length > 0) {
                writeChromatogram.WriteLine("BasePeak({0}points)\tRT\tIntensity", trace[0].Length);
                for (int i = 0; i < trace[0].Length; i++)
                    writeChromatogram.WriteLine("{0}\t{1:F3}\t{2:F0}", i, 60 * trace[0].Times[i], trace[0].Intensities[i]);
            }
            // per-scan
            int scanCount = lastScan - firstScan + 1;
            int ionsAboveThreshold = 0; double tic = 0;
            // intensities[k] and scanTimes[k] hold the max-intensity and RT for scan k, used for FFT below
            var intensities = new double[scanCount];
            var scanTimes = new double[scanCount];
            // mzGrid accumulates summed MS1 intensity on a 0.01 Da grid for mass-domain FFT
            const double mzBinSize = 0.01;
            double mzMin = rawFile.RunHeaderEx.LowMass, mzMax = rawFile.RunHeaderEx.HighMass;
            int mzGridSize = (int)Math.Ceiling((mzMax - mzMin) / mzBinSize) + 1;
            var mzGrid = new double[mzGridSize];
            int ms1ScanCount = 0;
            using var writeMS1 = new StreamWriter(rawFile.FileName + ".profile.intensity" + intensityThreshold + ".charge" + chargeThreshold + ".MS.txt");
            using var writeMS2 = new StreamWriter(rawFile.FileName + ".centroid.MGF");
            using var writeMS2Profile = new StreamWriter(rawFile.FileName + ".profile.MGF");
            for (int scanNumber = firstScan; scanNumber <= lastScan; scanNumber++) {
                double time = rawFile.RetentionTimeFromScanNumber(scanNumber);
                string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(scanNumber));
                var scanStats = rawFile.GetScanStatsForScanNumber(scanNumber);
                var segScan = rawFile.GetSegmentedScanFromScanNumber(scanNumber, scanStats);
                var centroid = rawFile.GetCentroidStream(scanNumber, false);
                double maxMass = 0, maxIntensity = 0, maxIntensitySum = 0;
                string charge = "";
                var trailer = rawFile.GetTrailerExtraInformation(scanNumber);
                for (int l = 0; l < trailer.Length; l++)
                    if (trailer.Labels[l] == "Charge State:") charge = trailer.Values[l];
                if (scanStats.IsCentroidScan) {
                    double precursor = rawFile.GetScanEventForScanNumber(scanNumber).GetReaction(0).PrecursorMass;
                    writeMS2.WriteLine("BEGIN IONS\nTITLE={0}\t{3}\tSCANS={2}\nRTINSECONDS={1}\nPEPMASS={6}\t{4}\t{5}\nCHARGE={7}+",
                        scanNumber, time * 60, segScan.Positions.Length, title,
                        scanStats.BasePeakMass, scanStats.BasePeakIntensity, precursor, charge);
                    for (int j = 0; j < segScan.Positions.Length; j++)
                        writeMS2.WriteLine("{0} {1}", segScan.Positions[j], segScan.Intensities[j]);
                    writeMS2.WriteLine("END IONS\n");
                } else if (!title.Contains(" ms ", StringComparison.Ordinal)) {
                    double precursor = rawFile.GetScanEventForScanNumber(scanNumber).GetReaction(0).PrecursorMass;
                    writeMS2Profile.WriteLine("BEGIN IONS\nTITLE={0}\t{1}\tSCANS={2}\nRTINSECONDS={3}\nPEPMASS={4}\nCHARGE={5}+",
                        scanNumber, title, centroid.Length, time * 60, precursor, charge);
                    for (int j = 0; j < centroid.Length; j++)
                        writeMS2Profile.WriteLine("{0} {1}", centroid.Masses[j], centroid.Intensities[j]);
                    writeMS2Profile.WriteLine("END IONS\n");
                } else {
                    writeMS1.WriteLine("Scan{0}\tMZ\tcharge\tintensity\t{1}\t{2}", scanNumber, title, centroid.Length);
                    for (int j = 0; j < centroid.Length; j++) {
                        double mass = centroid.Masses[j], intensity = centroid.Intensities[j];
                        int z = (int)centroid.Charges[j];
                        if (z >= chargeThreshold && intensity >= intensityThreshold) {
                            writeMS1.WriteLine("{0}\t{1}\t{3}\t{2}\t{4}", j, mass, intensity, z, mass * z - z);
                            tic += intensity; ionsAboveThreshold++;
                        }
                        if (intensity >= maxIntensity) { maxIntensity = intensity; maxMass = mass; maxIntensitySum += maxIntensity; }
                    }
                    // accumulate MS1 centroids onto the regular m/z grid for the mass-domain FFT
                    for (int j = 0; j < centroid.Length; j++) {
                        int bin = (int)Math.Round((centroid.Masses[j] - mzMin) / mzBinSize);
                        if (bin >= 0 && bin < mzGridSize) mzGrid[bin] += centroid.Intensities[j];
                    }
                    ms1ScanCount++;
                }
                Console.WriteLine("{0}\t{8}\t{7}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}",
                    scanNumber, maxIntensitySum, title, maxMass, time, maxIntensity, charge,
                    scanStats.TIC, scanStats.BasePeakMass);
                intensities[scanNumber - firstScan] = maxIntensity;
                scanTimes[scanNumber - firstScan] = time;
            }
            // FFT of intensity-over-time: magnitude peak at bin k → periodic pattern every N/k scans
            var avgDt = scanCount > 1 ? (scanTimes[scanCount - 1] - scanTimes[0]) / (scanCount - 1) : 1.0; // minutes per scan
            var samples = new Complex[scanCount];
            for (int i = 0; i < scanCount; i++) samples[i] = new Complex(intensities[i], 0);
            Fourier.Forward(samples, FourierOptions.NoScaling);
            using var writeFFT = new StreamWriter(rawFile.FileName + ".intensity" + intensityThreshold + ".charge" + chargeThreshold + ".FFT.txt");
            writeFFT.WriteLine("bin\tfrequency_per_min\tperiod_min\tmagnitude");
            for (int i = 0; i < scanCount / 2; i++) {
                double mag = (2.0 / scanCount) * Math.Sqrt(samples[i].Real * samples[i].Real + samples[i].Imaginary * samples[i].Imaginary);
                double freqPerMin = i / (scanCount * avgDt); // cycles per minute
                double periodMin = i == 0 ? double.PositiveInfinity : 1.0 / freqPerMin;
                writeFFT.WriteLine("{0}\t{1:F6}\t{2:F4}\t{3:F2}", i, freqPerMin, periodMin, mag);
            }
            Console.WriteLine("#TIC>={0}intensity:\t{1}", intensityThreshold, tic);
            Console.WriteLine("#Ions>=charge{0}:\t{1}", chargeThreshold, ionsAboveThreshold);
            // mass-domain FFT of averaged MS1 spectrum: peak at bin k → m/z spacing = mzGridSize*mzBinSize/k Da
            if (ms1ScanCount > 0) {
                var mzSamples = new Complex[mzGridSize];
                for (int i = 0; i < mzGridSize; i++) mzSamples[i] = new Complex(mzGrid[i] / ms1ScanCount, 0);
                Fourier.Forward(mzSamples, FourierOptions.NoScaling);
                using var writeMzFFT = new StreamWriter(rawFile.FileName + ".mzFFT.txt");
                writeMzFFT.WriteLine("bin\tspacing_Da\tmagnitude");
                // only write first N/2 bins; skip bin 0 (DC = average intensity)
                for (int i = 1; i < mzGridSize / 2; i++) {
                    double mag = (2.0 / mzGridSize) * Math.Sqrt(mzSamples[i].Real * mzSamples[i].Real + mzSamples[i].Imaginary * mzSamples[i].Imaginary);
                    double spacingDa = (mzGridSize * mzBinSize) / i;
                    writeMzFFT.WriteLine("{0}\t{1:F4}\t{2:F2}", i, spacingDa, mag);
                }
            }
        } finally { rawFile.Dispose(); }
    }
}
