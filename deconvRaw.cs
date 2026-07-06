// compile: mcs deconvRaw.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:deconvRaw.exe
// run IgL discovery: mono deconvRaw.exe 260629_Solveig_3_L.raw 18000 25000 8 40 10 1000000 10000000 0.85 5 3 2 35 12 0 -1 -1 -1 -1 0 0.05 3 0.20 3 300 8 20.0 0.70 1.0 0.01 > log.txt 2>&1
// args: rawFile minMass maxMass minCharge maxCharge ppmTolerance minSeedIntensity minEnvelopeIntensity minCos minMatchedIsotopes minFeatureScans maxGapScans maxSeedIsotopeIndex threads writeEvidence minRt maxRt minMz maxMz minTraceLengthSeconds minSampleRate minChargeCount minFeatureScore seedIsoWindow maxSeedPeaks isotopeCollapseMaxShift collapseApexToleranceMin collapseRtOverlapFraction sameMassRtGapMin weakSameMassRelativeIntensity
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.Data.Business;
namespace DeconvRawDiscovery
{
    internal class DeconvRaw
    {
        private const double Proton = 1.007276466812;
        private const double C13MinusC12 = 1.00335483507;
        private const double AveragineCacheBinDa = 10.0;

        private static int ProgressTotal = 0;
        private static int ProgressDone = 0;
        private static int ProgressLastPercent = -1;
        private static readonly object ProgressLock = new object();

        // CHANGE: cache averagine envelopes to avoid recomputing isotope models repeatedly.
        private static readonly ConcurrentDictionary<int, double[]> AveragineCache = new ConcurrentDictionary<int, double[]>();

        private class ScanRange { public int Start; public int End; }
        private class WorkerResult { public List<ScanSummary> ScanSummaries = new List<ScanSummary>(); public List<EnvelopeHit> Evidence = new List<EnvelopeHit>(); }
        private class PeakHit { public int Index; public double Mz; public double Intensity; }

        private class EnvelopeHit
        {
            public int Scan; public double Rt; public double CandidateMass; public double ObservedMass; public double PpmError; public int Charge; public int SeedIsotopeIndex;
            public double BaseMz; public double EnvelopeIntensity; public double MaxIsotopeIntensity; public int ApexIsotopeIndex; public int MatchedIsotopeCount; public int TotalIsotopeCount;
            public double IsotopeCosineScore; public double BasePeakMass; public double Tic;
        }

        private class ScanSummary
        {
            public int Scan; public double Rt; public double Mass; public double MedianPpmError; public double ScanEnvelopeIntensity; public double MaxChargeEnvelopeIntensity;
            public int BestCharge; public double BestChargeCosine; public double MedianIsotopeCosine; public int MedianMatchedIsotopes;
            public int MinCharge; public int MaxCharge; public int ChargeCount; public double ChargeContinuity; public int EvidenceCount;
        }

        private class FeatureGroup
        {
            public int FeatureIndex; public int RepresentativeFeatureIndex; public int MergedFeatureCount; public string MergedIsotopeOffsets;
            public double Mass; public double PreferredMass; public double RawMergedMassMin; public double RawMergedMassMax; public double RawMergedMassSpanDa;
            public bool IsotopeAnchorAmbiguous; public string MassInterpretation;
            public List<ScanSummary> Scans = new List<ScanSummary>(); public bool Accepted; public string RejectReason;
            public double StartRt; public double EndRt; public double ApexRt; public double TraceLengthSeconds; public double SumIntensity; public double MaxScanIntensity;
            public int MinCharge; public int MaxCharge; public int ChargeCount; public double ChargeContinuity; public int MatchedScans; public int ScanSpan; public double SampleRate;
            public double MedianPpmError; public double PpmMad; public double MedianIsotopeCosine; public double BestIsotopeCosine; public int MedianMatchedIsotopes;
            public double ApexDominance; public double FeatureScore;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("USAGE:");
                Console.WriteLine("{0} file.raw [minMass=10000] [maxMass=100000] [minCharge=1] [maxCharge=80] [ppmTolerance=10] [minSeedIntensity=1000000] [minEnvelopeIntensity=10000000] [minCos=0.85] [minMatchedIsotopes=5] [minFeatureScans=3] [maxGapScans=2] [maxSeedIsotopeIndex=45] [threads=0] [writeEvidence=0] [minRt=-1] [maxRt=-1] [minMz=-1] [maxMz=-1] [minTraceLengthSeconds=0] [minSampleRate=0.05] [minChargeCount=1] [minFeatureScore=0] [seedIsoWindow=3] [maxSeedPeaks=300] [isotopeCollapseMaxShift=8] [collapseApexToleranceMin=20] [collapseRtOverlapFraction=0.70] [sameMassRtGapMin=1.0] [weakSameMassRelativeIntensity=0.01]", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];
            double minMass = 10000.0, maxMass = 100000.0, ppmTolerance = 10.0, minSeedIntensity = 1000000.0, minEnvelopeIntensity = 10000000.0, minCos = 0.85;
            double minRt = -1.0, maxRt = -1.0, minMz = -1.0, maxMz = -1.0, minTraceLengthSeconds = 0.0, minSampleRate = 0.05, minFeatureScore = 0.0;
            double collapseApexToleranceMin = 20.0, collapseRtOverlapFraction = 0.70, sameMassRtGapMin = 1.0, weakSameMassRelativeIntensity = 0.01;
            int minCharge = 1, maxCharge = 80, minMatchedIsotopes = 5, minFeatureScans = 3, maxGapScans = 2, maxSeedIsotopeIndex = 45, threads = 0, writeEvidenceInt = 0;
            int minChargeCount = 1, seedIsoWindow = 3, maxSeedPeaks = 300, isotopeCollapseMaxShift = 8;

            if (args.Length >= 2) double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out minMass);
            if (args.Length >= 3) double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out maxMass);
            if (args.Length >= 4) int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out minCharge);
            if (args.Length >= 5) int.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxCharge);
            if (args.Length >= 6) double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out ppmTolerance);
            if (args.Length >= 7) double.TryParse(args[6], NumberStyles.Float, CultureInfo.InvariantCulture, out minSeedIntensity);
            if (args.Length >= 8) double.TryParse(args[7], NumberStyles.Float, CultureInfo.InvariantCulture, out minEnvelopeIntensity);
            if (args.Length >= 9) double.TryParse(args[8], NumberStyles.Float, CultureInfo.InvariantCulture, out minCos);
            if (args.Length >= 10) int.TryParse(args[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out minMatchedIsotopes);
            if (args.Length >= 11) int.TryParse(args[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out minFeatureScans);
            if (args.Length >= 12) int.TryParse(args[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxGapScans);
            if (args.Length >= 13) int.TryParse(args[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxSeedIsotopeIndex);
            if (args.Length >= 14) int.TryParse(args[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out threads);
            if (args.Length >= 15) int.TryParse(args[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out writeEvidenceInt);
            if (args.Length >= 16) double.TryParse(args[15], NumberStyles.Float, CultureInfo.InvariantCulture, out minRt);
            if (args.Length >= 17) double.TryParse(args[16], NumberStyles.Float, CultureInfo.InvariantCulture, out maxRt);
            if (args.Length >= 18) double.TryParse(args[17], NumberStyles.Float, CultureInfo.InvariantCulture, out minMz);
            if (args.Length >= 19) double.TryParse(args[18], NumberStyles.Float, CultureInfo.InvariantCulture, out maxMz);
            if (args.Length >= 20) double.TryParse(args[19], NumberStyles.Float, CultureInfo.InvariantCulture, out minTraceLengthSeconds);
            if (args.Length >= 21) double.TryParse(args[20], NumberStyles.Float, CultureInfo.InvariantCulture, out minSampleRate);
            if (args.Length >= 22) int.TryParse(args[21], NumberStyles.Integer, CultureInfo.InvariantCulture, out minChargeCount);
            if (args.Length >= 23) double.TryParse(args[22], NumberStyles.Float, CultureInfo.InvariantCulture, out minFeatureScore);
            if (args.Length >= 24) int.TryParse(args[23], NumberStyles.Integer, CultureInfo.InvariantCulture, out seedIsoWindow);
            if (args.Length >= 25) int.TryParse(args[24], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxSeedPeaks);
            if (args.Length >= 26) int.TryParse(args[25], NumberStyles.Integer, CultureInfo.InvariantCulture, out isotopeCollapseMaxShift);
            if (args.Length >= 27) double.TryParse(args[26], NumberStyles.Float, CultureInfo.InvariantCulture, out collapseApexToleranceMin);
            if (args.Length >= 28) double.TryParse(args[27], NumberStyles.Float, CultureInfo.InvariantCulture, out collapseRtOverlapFraction);
            if (args.Length >= 29) double.TryParse(args[28], NumberStyles.Float, CultureInfo.InvariantCulture, out sameMassRtGapMin);
            if (args.Length >= 30) double.TryParse(args[29], NumberStyles.Float, CultureInfo.InvariantCulture, out weakSameMassRelativeIntensity);

            if (minCharge <= 0) minCharge = 1; if (maxCharge < minCharge) maxCharge = minCharge; if (maxSeedIsotopeIndex < 0) maxSeedIsotopeIndex = 0;
            if (threads <= 0) threads = Environment.ProcessorCount; if (threads < 1) threads = 1;
            if (minSampleRate < 0.0) minSampleRate = 0.0; if (minSampleRate > 1.0) minSampleRate = 1.0; if (minChargeCount < 1) minChargeCount = 1;
            if (seedIsoWindow < 0) seedIsoWindow = -1; if (maxSeedPeaks < 0) maxSeedPeaks = 0; if (isotopeCollapseMaxShift < 0) isotopeCollapseMaxShift = 0;
            if (collapseRtOverlapFraction < 0.0) collapseRtOverlapFraction = 0.0; if (collapseRtOverlapFraction > 1.0) collapseRtOverlapFraction = 1.0;
            if (sameMassRtGapMin < 0.0) sameMassRtGapMin = 0.0; if (weakSameMassRelativeIntensity < 0.0) weakSameMassRelativeIntensity = 0.0;
            bool writeEvidence = writeEvidenceInt != 0;

            var rawFile = RawFileReaderAdapter.FileFactory(rawPath);
            if (!rawFile.IsOpen || rawFile.IsError) { Console.Error.WriteLine("Error opening raw file: {0} FileError: {1}", rawPath, rawFile.FileError); return; }
            rawFile.SelectInstrument(Device.MS, 1);
            int firstScan = rawFile.RunHeaderEx.FirstSpectrum, lastScan = rawFile.RunHeaderEx.LastSpectrum, scanCount = lastScan - firstScan + 1;

            Console.WriteLine("#filename:\t{0}", rawFile.FileName); Console.WriteLine("#scans:\t{0}", scanCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minMass:\t{0}", minMass.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#maxMass:\t{0}", maxMass.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minCharge:\t{0}", minCharge.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#maxCharge:\t{0}", maxCharge.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#ppmTolerance:\t{0}", ppmTolerance.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#minSeedIntensity:\t{0}", minSeedIntensity.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minEnvelopeIntensity:\t{0}", minEnvelopeIntensity.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#minCos:\t{0}", minCos.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minMatchedIsotopes:\t{0}", minMatchedIsotopes.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#minFeatureScans:\t{0}", minFeatureScans.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxGapScans:\t{0}", maxGapScans.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#maxSeedIsotopeIndex:\t{0}", maxSeedIsotopeIndex.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#threads:\t{0}", threads.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#writeEvidence:\t{0}", writeEvidence ? "1" : "0");
            Console.WriteLine("#minRt:\t{0}", minRt.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#maxRt:\t{0}", maxRt.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minMz:\t{0}", minMz.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#maxMz:\t{0}", maxMz.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minTraceLengthSeconds:\t{0}", minTraceLengthSeconds.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#minSampleRate:\t{0}", minSampleRate.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minChargeCount:\t{0}", minChargeCount.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#minFeatureScore:\t{0}", minFeatureScore.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#seedIsoWindow:\t{0}", seedIsoWindow.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#maxSeedPeaks:\t{0}", maxSeedPeaks.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#isotopeCollapseMaxShift:\t{0}", isotopeCollapseMaxShift.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#collapseApexToleranceMin:\t{0}", collapseApexToleranceMin.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#collapseRtOverlapFraction:\t{0}", collapseRtOverlapFraction.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("#sameMassRtGapMin:\t{0}", sameMassRtGapMin.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#weakSameMassRelativeIntensity:\t{0}", weakSameMassRelativeIntensity.ToString(CultureInfo.InvariantCulture));
            rawFile.Dispose();

            ProgressTotal = scanCount; ProgressDone = 0; ProgressLastPercent = -1;
            int chunkSize = Math.Max(10, scanCount / Math.Max(1, threads * 8));
            List<ScanRange> ranges = BuildScanRanges(firstScan, lastScan, chunkSize);
            var workerResults = new ConcurrentBag<WorkerResult>();

            Parallel.ForEach(ranges, new ParallelOptions { MaxDegreeOfParallelism = threads }, range =>
            {
                WorkerResult result = ProcessScanRange(rawPath, range.Start, range.End, minMass, maxMass, minCharge, maxCharge, ppmTolerance, minSeedIntensity, minEnvelopeIntensity, minCos, minMatchedIsotopes, maxSeedIsotopeIndex, seedIsoWindow, maxSeedPeaks, writeEvidence, minRt, maxRt, minMz, maxMz);
                workerResults.Add(result);
            });
            Console.WriteLine();

            List<ScanSummary> allScanSummaries = workerResults.SelectMany(r => r.ScanSummaries).OrderBy(s => s.Mass).ThenBy(s => s.Scan).ToList();
            List<EnvelopeHit> allEvidence = writeEvidence ? workerResults.SelectMany(r => r.Evidence).ToList() : new List<EnvelopeHit>();
            List<FeatureGroup> features = BuildGlobalFeatures(allScanSummaries, ppmTolerance, minFeatureScans, maxGapScans, minTraceLengthSeconds, minSampleRate, minChargeCount, minFeatureScore);
            List<FeatureGroup> acceptedRaw = features.Where(f => f.Accepted).OrderByDescending(f => f.SumIntensity).ToList();
            int rawAcceptedIndex = 1; foreach (FeatureGroup f in acceptedRaw) { f.FeatureIndex = rawAcceptedIndex; f.RepresentativeFeatureIndex = rawAcceptedIndex; f.MergedFeatureCount = 1; f.MergedIsotopeOffsets = "0"; rawAcceptedIndex++; }
            List<FeatureGroup> rejected = features.Where(f => !f.Accepted).OrderByDescending(f => f.SumIntensity).ToList();
            int rejectedIndex = 1; foreach (FeatureGroup f in rejected) { f.FeatureIndex = rejectedIndex; f.RepresentativeFeatureIndex = rejectedIndex; f.MergedFeatureCount = 1; f.MergedIsotopeOffsets = "0"; rejectedIndex++; }

            string prefix = rawPath + ".discovery";
            string scanSummaryFile = prefix + ".scan_summary.tsv", featureFile = prefix + ".deconv_masses.tsv", uncollapsedFeatureFile = prefix + ".deconv_masses.uncollapsed.tsv", rejectedFile = prefix + ".rejected_masses.tsv", evidenceFile = prefix + ".scan_evidence.tsv";
            WriteFeatureSummary(uncollapsedFeatureFile, acceptedRaw.OrderByDescending(f => f.SumIntensity).ToList(), false);
            List<FeatureGroup> acceptedIsotopeCollapsed = CollapseIsotopeShiftedFeatures(acceptedRaw, ppmTolerance, isotopeCollapseMaxShift, collapseApexToleranceMin, collapseRtOverlapFraction);
            List<FeatureGroup> acceptedFinalCollapsed = CollapseWeakSameMassFragments(acceptedIsotopeCollapsed, ppmTolerance, sameMassRtGapMin, weakSameMassRelativeIntensity);
            int acceptedIndex = 1; foreach (FeatureGroup f in acceptedFinalCollapsed.OrderByDescending(f => f.SumIntensity)) { f.FeatureIndex = acceptedIndex; acceptedIndex++; }
            WriteScanSummary(scanSummaryFile, allScanSummaries);
            WriteFeatureSummary(featureFile, acceptedFinalCollapsed.OrderByDescending(f => f.SumIntensity).ToList(), false);
            WriteFeatureSummary(rejectedFile, rejected, true);
            if (writeEvidence) { WriteEvidence(evidenceFile, allEvidence); Console.WriteLine("Wrote scan evidence: {0}", evidenceFile); } else { Console.WriteLine("Skipped scan evidence output. Set writeEvidence=1 to enable."); }
            Console.WriteLine("Wrote scan summary: {0}", scanSummaryFile);
            Console.WriteLine("Wrote final isotope/same-mass collapsed deconvolved masses: {0}", featureFile);
            Console.WriteLine("Wrote uncollapsed accepted masses: {0}", uncollapsedFeatureFile);
            Console.WriteLine("Wrote rejected masses: {0}", rejectedFile);
        }

        private static List<ScanRange> BuildScanRanges(int firstScan, int lastScan, int chunkSize)
        {
            var ranges = new List<ScanRange>(); int start = firstScan;
            while (start <= lastScan) { int end = Math.Min(lastScan, start + chunkSize - 1); ranges.Add(new ScanRange { Start = start, End = end }); start = end + 1; }
            return ranges;
        }

        private static WorkerResult ProcessScanRange(string rawPath, int startScan, int endScan, double minMass, double maxMass, int minCharge, int maxCharge, double ppmTolerance, double minSeedIntensity, double minEnvelopeIntensity, double minCos, int minMatchedIsotopes, int maxSeedIsotopeIndex, int seedIsoWindow, int maxSeedPeaks, bool writeEvidence, double minRt, double maxRt, double minMz, double maxMz)
        {
            WorkerResult result = new WorkerResult(); var localRawFile = RawFileReaderAdapter.FileFactory(rawPath);
            if (!localRawFile.IsOpen || localRawFile.IsError) { Console.Error.WriteLine("Worker failed to open RAW file. FileError: {0}", localRawFile.FileError); return result; }
            localRawFile.SelectInstrument(Device.MS, 1);
            try
            {
                for (int scanNumber = startScan; scanNumber <= endScan; scanNumber++)
                {
                    try
                    {
                        double rt = localRawFile.RetentionTimeFromScanNumber(scanNumber);
                        if (minRt >= 0.0 && rt < minRt) { ReportProgress(); continue; }
                        if (maxRt >= 0.0 && rt > maxRt) { ReportProgress(); continue; }
                        var scanStatistics = localRawFile.GetScanStatsForScanNumber(scanNumber);
                        string scanEventText = ""; try { scanEventText = string.Join(" ", localRawFile.GetScanEventForScanNumber(scanNumber)); } catch { scanEventText = ""; }
                        bool looksLikeMs1 = scanEventText.Length == 0 || (scanEventText.IndexOf("ms", StringComparison.OrdinalIgnoreCase) >= 0 && scanEventText.IndexOf("@", StringComparison.OrdinalIgnoreCase) < 0);
                        if (!looksLikeMs1) { ReportProgress(); continue; }
                        CentroidStream centroidStream = null; try { centroidStream = localRawFile.GetCentroidStream(scanNumber, false); } catch { centroidStream = null; }
                        if (centroidStream == null || centroidStream.Length <= 0) { ReportProgress(); continue; }
                        List<EnvelopeHit> scanHits = DiscoverScanMasses(scanNumber, rt, scanStatistics, centroidStream, minMass, maxMass, minCharge, maxCharge, ppmTolerance, minSeedIntensity, minEnvelopeIntensity, minCos, minMatchedIsotopes, maxSeedIsotopeIndex, seedIsoWindow, maxSeedPeaks, minMz, maxMz);
                        if (scanHits.Count > 0) { List<ScanSummary> scanSummaries = CollapseScanHits(scanHits, ppmTolerance); result.ScanSummaries.AddRange(scanSummaries); if (writeEvidence) result.Evidence.AddRange(scanHits); }
                    }
                    catch (Exception ex) { Console.Error.WriteLine("Warning: failed scan {0}: {1}", scanNumber.ToString(CultureInfo.InvariantCulture), ex.Message); }
                    ReportProgress();
                }
            }
            finally { localRawFile.Dispose(); }
            return result;
        }

        private static List<EnvelopeHit> DiscoverScanMasses(int scanNumber, double rt, dynamic scanStatistics, CentroidStream centroidStream, double minMass, double maxMass, int minCharge, int maxCharge, double ppmTolerance, double minSeedIntensity, double minEnvelopeIntensity, double minCos, int minMatchedIsotopes, int maxSeedIsotopeIndex, int seedIsoWindow, int maxSeedPeaks, double minMz, double maxMz)
        {
            List<EnvelopeHit> hits = new List<EnvelopeHit>();
            double[] mzArray = centroidStream.Masses, intensityArray = centroidStream.Intensities, chargeArray = null;
            try { chargeArray = centroidStream.Charges; } catch { chargeArray = null; }
            if (mzArray == null || intensityArray == null || mzArray.Length != intensityArray.Length) return hits;
            List<int> seedPeakIndices = GetSeedPeakIndices(intensityArray, minSeedIntensity, maxSeedPeaks);
            for (int seedListIndex = 0; seedListIndex < seedPeakIndices.Count; seedListIndex++)
            {
                int peakIndex = seedPeakIndices[seedListIndex]; double seedMz = mzArray[peakIndex], seedIntensity = intensityArray[peakIndex];
                if (seedIntensity < minSeedIntensity) continue; if (minMz >= 0.0 && seedMz < minMz) continue; if (maxMz >= 0.0 && seedMz > maxMz) continue;
                List<int> charges = GetCandidateCharges(chargeArray, peakIndex, minCharge, maxCharge);
                for (int ci = 0; ci < charges.Count; ci++)
                {
                    int charge = charges[ci], startSeedIso, endSeedIso; GetSeedIsotopeRange(seedMz, charge, minMass, maxMass, maxSeedIsotopeIndex, seedIsoWindow, out startSeedIso, out endSeedIso);
                    for (int seedIso = startSeedIso; seedIso <= endSeedIso; seedIso++)
                    {
                        double candidateMass = seedMz * charge - charge * Proton - seedIso * C13MinusC12;
                        if (candidateMass < minMass || candidateMass > maxMass) continue;
                        EnvelopeHit hit = TryMatchEnvelope(scanNumber, rt, scanStatistics, candidateMass, charge, seedIso, mzArray, intensityArray, ppmTolerance, minEnvelopeIntensity, minCos, minMatchedIsotopes);
                        if (hit != null) hits.Add(hit);
                    }
                }
            }
            return hits;
        }

        private static List<int> GetSeedPeakIndices(double[] intensityArray, double minSeedIntensity, int maxSeedPeaks)
        {
            List<int> indices = new List<int>(); for (int i = 0; i < intensityArray.Length; i++) if (intensityArray[i] >= minSeedIntensity) indices.Add(i);
            indices = indices.OrderByDescending(i => intensityArray[i]).ToList(); if (maxSeedPeaks > 0 && indices.Count > maxSeedPeaks) indices = indices.Take(maxSeedPeaks).ToList(); return indices;
        }
        private static void GetSeedIsotopeRange(double seedMz, int charge, double minMass, double maxMass, int maxSeedIsotopeIndex, int seedIsoWindow, out int startSeedIso, out int endSeedIso)
        {
            if (seedIsoWindow < 0) { startSeedIso = 0; endSeedIso = maxSeedIsotopeIndex; return; }
            double seedNeutral = seedMz * charge - charge * Proton, clippedMass = seedNeutral; if (clippedMass < minMass) clippedMass = minMass; if (clippedMass > maxMass) clippedMass = maxMass;
            int estimatedApex = EstimateAveragineApexIndex(clippedMass); startSeedIso = estimatedApex - seedIsoWindow; endSeedIso = estimatedApex + seedIsoWindow; if (startSeedIso < 0) startSeedIso = 0; if (endSeedIso > maxSeedIsotopeIndex) endSeedIso = maxSeedIsotopeIndex;
        }
        private static int EstimateAveragineApexIndex(double neutralMass) { double lambda = neutralMass / 1800.0; int apex = (int)Math.Round(lambda); if (apex < 0) apex = 0; return apex; }
        private static List<int> GetCandidateCharges(double[] chargeArray, int peakIndex, int minCharge, int maxCharge)
        {
            List<int> charges = new List<int>(); int thermoCharge = 0; try { if (chargeArray != null && peakIndex < chargeArray.Length) thermoCharge = (int)Math.Round(chargeArray[peakIndex]); } catch { thermoCharge = 0; }
            if (thermoCharge >= minCharge && thermoCharge <= maxCharge) charges.Add(thermoCharge); else for (int z = minCharge; z <= maxCharge; z++) charges.Add(z); return charges;
        }

        private static EnvelopeHit TryMatchEnvelope(int scanNumber, double rt, dynamic scanStatistics, double candidateMass, int charge, int seedIsotopeIndex, double[] mzArray, double[] intensityArray, double ppmTolerance, double minEnvelopeIntensity, double minCos, int minMatchedIsotopes)
        {
            if (charge <= 0) return null; int isotopeCount = EstimateIsotopeCount(candidateMass); double[] theoretical = GetCachedAveragineEnvelope(candidateMass, isotopeCount), observed = new double[isotopeCount];
            double envelopeIntensity = 0.0, maxIsotopeIntensity = 0.0, observedMassWeightedSum = 0.0, observedMassWeight = 0.0; int apexIsotopeIndex = -1, matchedIsotopeCount = 0;
            for (int isotopeIndex = 0; isotopeIndex < isotopeCount; isotopeIndex++)
            {
                double expectedMz = (candidateMass + isotopeIndex * C13MinusC12 + charge * Proton) / charge, toleranceDa = expectedMz * ppmTolerance / 1e6;
                PeakHit peak = FindHighestPeak(mzArray, intensityArray, expectedMz - toleranceDa, expectedMz + toleranceDa);
                if (peak != null)
                {
                    observed[isotopeIndex] = peak.Intensity; envelopeIntensity += peak.Intensity; matchedIsotopeCount++;
                    if (peak.Intensity > maxIsotopeIntensity) { maxIsotopeIntensity = peak.Intensity; apexIsotopeIndex = isotopeIndex; }
                    double observedNeutralMassForThisIsotope = peak.Mz * charge - charge * Proton - isotopeIndex * C13MinusC12; observedMassWeightedSum += observedNeutralMassForThisIsotope * peak.Intensity; observedMassWeight += peak.Intensity;
                }
            }
            if (matchedIsotopeCount < minMatchedIsotopes) return null; if (envelopeIntensity < minEnvelopeIntensity) return null; double cosine = Cosine(observed, theoretical); if (cosine < minCos) return null;
            double observedMass = observedMassWeight > 0.0 ? observedMassWeightedSum / observedMassWeight : double.NaN, ppmError = PpmError(observedMass, candidateMass), baseMz = (candidateMass + charge * Proton) / charge;
            double basePeakMass = double.NaN, tic = double.NaN; try { basePeakMass = scanStatistics.BasePeakMass; tic = scanStatistics.TIC; } catch { }
            return new EnvelopeHit { Scan = scanNumber, Rt = rt, CandidateMass = candidateMass, ObservedMass = observedMass, PpmError = ppmError, Charge = charge, SeedIsotopeIndex = seedIsotopeIndex, BaseMz = baseMz, EnvelopeIntensity = envelopeIntensity, MaxIsotopeIntensity = maxIsotopeIntensity, ApexIsotopeIndex = apexIsotopeIndex, MatchedIsotopeCount = matchedIsotopeCount, TotalIsotopeCount = isotopeCount, IsotopeCosineScore = cosine, BasePeakMass = basePeakMass, Tic = tic };
        }

        private static int EstimateIsotopeCount(double neutralMass) { double lambda = neutralMass / 1800.0; int count = (int)Math.Ceiling(lambda + 8.0 * Math.Sqrt(Math.Max(1.0, lambda)) + 12.0); if (count < 16) count = 16; if (count > 160) count = 160; return count; }
        private static double[] GetCachedAveragineEnvelope(double neutralMass, int isotopeCount) { int massBin = (int)Math.Round(neutralMass / AveragineCacheBinDa), key = isotopeCount * 1000000 + massBin; return AveragineCache.GetOrAdd(key, k => BuildAveragineEnvelope(massBin * AveragineCacheBinDa, isotopeCount)); }
        private static double[] BuildAveragineEnvelope(double neutralMass, int isotopeCount)
        {
            double scale = neutralMass / 111.1254; int carbon = Math.Max(1, (int)Math.Round(4.9384 * scale)), hydrogen = Math.Max(1, (int)Math.Round(7.7583 * scale)), nitrogen = Math.Max(1, (int)Math.Round(1.3577 * scale)), oxygen = Math.Max(1, (int)Math.Round(1.4773 * scale)), sulfur = Math.Max(0, (int)Math.Round(0.0417 * scale));
            double[] envelope = new double[isotopeCount]; envelope[0] = 1.0;
            envelope = Convolve(envelope, BinomialDistribution(carbon, 0.0107, isotopeCount), isotopeCount); envelope = Convolve(envelope, BinomialDistribution(hydrogen, 0.000115, isotopeCount), isotopeCount); envelope = Convolve(envelope, BinomialDistribution(nitrogen, 0.00364, isotopeCount), isotopeCount); envelope = Convolve(envelope, MultinomialSmallElementDistribution(oxygen, new int[] { 0, 1, 2 }, new double[] { 0.99757, 0.00038, 0.00205 }, isotopeCount), isotopeCount);
            if (sulfur > 0) envelope = Convolve(envelope, MultinomialSmallElementDistribution(sulfur, new int[] { 0, 1, 2, 4 }, new double[] { 0.9499, 0.0075, 0.0425, 0.0001 }, isotopeCount), isotopeCount);
            Normalize(envelope); return envelope;
        }
        private static double[] BinomialDistribution(int n, double p, int isotopeCount) { double[] d = new double[isotopeCount]; if (n <= 0) { d[0] = 1.0; return d; } double q = 1.0 - p; d[0] = Math.Exp(n * Math.Log(q)); for (int k = 1; k < isotopeCount; k++) d[k] = d[k - 1] * (n - k + 1) / k * p / q; Normalize(d); return d; }
        private static double[] MultinomialSmallElementDistribution(int n, int[] shifts, double[] probs, int isotopeCount) { double[] d = new double[isotopeCount]; d[0] = 1.0; for (int atom = 0; atom < n; atom++) { double[] next = new double[isotopeCount]; for (int i = 0; i < isotopeCount; i++) { if (d[i] == 0.0) continue; for (int s = 0; s < shifts.Length; s++) { int j = i + shifts[s]; if (j < isotopeCount) next[j] += d[i] * probs[s]; } } d = next; } Normalize(d); return d; }
        private static double[] Convolve(double[] a, double[] b, int isotopeCount) { double[] c = new double[isotopeCount]; for (int i = 0; i < isotopeCount; i++) { if (a[i] == 0.0) continue; for (int j = 0; j + i < isotopeCount; j++) { if (b[j] == 0.0) continue; c[i + j] += a[i] * b[j]; } } Normalize(c); return c; }
        private static void Normalize(double[] values) { double sum = values.Sum(); if (sum <= 0.0) return; for (int i = 0; i < values.Length; i++) values[i] /= sum; }

        private static PeakHit FindHighestPeak(double[] mzArray, double[] intensityArray, double minMz, double maxMz) { int start = LowerBound(mzArray, minMz); PeakHit best = null; for (int i = start; i < mzArray.Length; i++) { if (mzArray[i] > maxMz) break; if (mzArray[i] < minMz) continue; if (best == null || intensityArray[i] > best.Intensity) best = new PeakHit { Index = i, Mz = mzArray[i], Intensity = intensityArray[i] }; } return best; }
        private static int LowerBound(double[] array, double value) { int left = 0, right = array.Length; while (left < right) { int mid = left + (right - left) / 2; if (array[mid] < value) left = mid + 1; else right = mid; } return left; }

        private static List<ScanSummary> CollapseScanHits(List<EnvelopeHit> scanHits, double ppmTolerance)
        {
            List<ScanSummary> output = new List<ScanSummary>(); if (scanHits.Count == 0) return output; List<EnvelopeHit> sorted = scanHits.OrderBy(h => h.ObservedMass).ToList(); List<EnvelopeHit> current = new List<EnvelopeHit>(); double currentCenter = double.NaN;
            for (int i = 0; i < sorted.Count; i++) { EnvelopeHit hit = sorted[i]; if (double.IsNaN(hit.ObservedMass)) continue; if (current.Count == 0) { current.Add(hit); currentCenter = hit.ObservedMass; continue; } double ppm = Math.Abs(PpmError(hit.ObservedMass, currentCenter)); if (ppm <= ppmTolerance) { current.Add(hit); currentCenter = WeightedMeanMass(current); } else { output.Add(MakeScanSummary(current)); current.Clear(); current.Add(hit); currentCenter = hit.ObservedMass; } }
            if (current.Count > 0) output.Add(MakeScanSummary(current)); return output;
        }
        private static ScanSummary MakeScanSummary(List<EnvelopeHit> hits)
        {
            Dictionary<int, EnvelopeHit> bestByCharge = new Dictionary<int, EnvelopeHit>(); foreach (EnvelopeHit h in hits) if (!bestByCharge.ContainsKey(h.Charge) || h.EnvelopeIntensity > bestByCharge[h.Charge].EnvelopeIntensity) bestByCharge[h.Charge] = h;
            List<EnvelopeHit> list = bestByCharge.Values.ToList(); double totalIntensity = list.Sum(h => h.EnvelopeIntensity), mass = totalIntensity > 0.0 ? list.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / totalIntensity : list.Average(h => h.ObservedMass); EnvelopeHit best = list.OrderByDescending(h => h.EnvelopeIntensity).First(); List<int> charges = list.Select(h => h.Charge).Distinct().OrderBy(z => z).ToList(); int minCharge = charges.First(), maxCharge = charges.Last(); double chargeContinuity = (maxCharge >= minCharge) ? charges.Count / (double)(maxCharge - minCharge + 1) : 1.0;
            return new ScanSummary { Scan = best.Scan, Rt = best.Rt, Mass = mass, MedianPpmError = Median(list.Select(h => h.PpmError).ToList()), ScanEnvelopeIntensity = totalIntensity, MaxChargeEnvelopeIntensity = best.EnvelopeIntensity, BestCharge = best.Charge, BestChargeCosine = best.IsotopeCosineScore, MedianIsotopeCosine = Median(list.Select(h => h.IsotopeCosineScore).ToList()), MedianMatchedIsotopes = (int)Math.Round(Median(list.Select(h => (double)h.MatchedIsotopeCount).ToList())), MinCharge = minCharge, MaxCharge = maxCharge, ChargeCount = charges.Count, ChargeContinuity = chargeContinuity, EvidenceCount = hits.Count };
        }
        private static double WeightedMeanMass(List<EnvelopeHit> hits) { double total = hits.Sum(h => h.EnvelopeIntensity); return total <= 0.0 ? hits.Average(h => h.ObservedMass) : hits.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / total; }

        private static List<FeatureGroup> BuildGlobalFeatures(List<ScanSummary> scanSummaries, double ppmTolerance, int minFeatureScans, int maxGapScans, double minTraceLengthSeconds, double minSampleRate, int minChargeCount, double minFeatureScore)
        {
            List<FeatureGroup> output = new List<FeatureGroup>(); if (scanSummaries.Count == 0) return output; List<ScanSummary> sortedByMass = scanSummaries.OrderBy(s => s.Mass).ToList(); List<List<ScanSummary>> massClusters = new List<List<ScanSummary>>(); List<ScanSummary> current = new List<ScanSummary>(); double centerMass = double.NaN;
            foreach (ScanSummary s in sortedByMass) { if (double.IsNaN(s.Mass)) continue; if (current.Count == 0) { current.Add(s); centerMass = s.Mass; continue; } double ppm = Math.Abs(PpmError(s.Mass, centerMass)); if (ppm <= ppmTolerance) { current.Add(s); centerMass = WeightedMeanMassFromScans(current); } else { massClusters.Add(current); current = new List<ScanSummary>(); current.Add(s); centerMass = s.Mass; } }
            if (current.Count > 0) massClusters.Add(current);
            foreach (List<ScanSummary> massCluster in massClusters) { List<ScanSummary> ordered = massCluster.OrderBy(s => s.Scan).ToList(); FeatureGroup feature = null; ScanSummary previous = null; foreach (ScanSummary scan in ordered) { bool startNew = false; if (feature == null) startNew = true; else if (previous != null && scan.Scan - previous.Scan > maxGapScans) startNew = true; if (startNew) { if (feature != null) output.Add(FinalizeFeature(feature, minFeatureScans, minTraceLengthSeconds, minSampleRate, minChargeCount, minFeatureScore)); feature = new FeatureGroup(); } feature.Scans.Add(scan); previous = scan; } if (feature != null) output.Add(FinalizeFeature(feature, minFeatureScans, minTraceLengthSeconds, minSampleRate, minChargeCount, minFeatureScore)); }
            return output;
        }
        private static FeatureGroup FinalizeFeature(FeatureGroup feature, int minFeatureScans, double minTraceLengthSeconds, double minSampleRate, int minChargeCount, double minFeatureScore)
        {
            List<ScanSummary> scans = feature.Scans.OrderBy(s => s.Scan).ToList(); double totalIntensity = scans.Sum(s => s.ScanEnvelopeIntensity); feature.SumIntensity = totalIntensity; feature.MaxScanIntensity = scans.Max(s => s.ScanEnvelopeIntensity); feature.Mass = totalIntensity > 0.0 ? scans.Sum(s => s.Mass * s.ScanEnvelopeIntensity) / totalIntensity : scans.Average(s => s.Mass); feature.StartRt = scans.First().Rt; feature.EndRt = scans.Last().Rt; feature.ApexRt = scans.OrderByDescending(s => s.ScanEnvelopeIntensity).First().Rt; feature.TraceLengthSeconds = Math.Max(0.0, (feature.EndRt - feature.StartRt) * 60.0); feature.MinCharge = scans.Min(s => s.MinCharge); feature.MaxCharge = scans.Max(s => s.MaxCharge); feature.ChargeCount = CountDistinctCharges(scans); feature.ChargeContinuity = feature.MaxCharge >= feature.MinCharge ? feature.ChargeCount / (double)(feature.MaxCharge - feature.MinCharge + 1) : 1.0; feature.MatchedScans = scans.Select(s => s.Scan).Distinct().Count(); feature.ScanSpan = scans.Last().Scan - scans.First().Scan + 1; feature.SampleRate = feature.ScanSpan > 0 ? feature.MatchedScans / (double)feature.ScanSpan : 1.0; feature.MedianPpmError = Median(scans.Select(s => s.MedianPpmError).ToList()); feature.PpmMad = MedianAbsoluteDeviation(scans.Select(s => s.MedianPpmError).ToList()); feature.MedianIsotopeCosine = Median(scans.Select(s => s.MedianIsotopeCosine).ToList()); feature.BestIsotopeCosine = scans.Max(s => s.BestChargeCosine); feature.MedianMatchedIsotopes = (int)Math.Round(Median(scans.Select(s => (double)s.MedianMatchedIsotopes).ToList())); feature.ApexDominance = feature.SumIntensity > 0.0 ? feature.MaxScanIntensity / feature.SumIntensity : 0.0; feature.FeatureScore = CalculateFeatureScore(feature); feature.MergedFeatureCount = 1; feature.MergedIsotopeOffsets = "0"; feature.RepresentativeFeatureIndex = feature.FeatureIndex;
            List<string> reasons = new List<string>(); if (feature.MatchedScans < minFeatureScans) reasons.Add("scan_count_below_minimum"); if (feature.TraceLengthSeconds < minTraceLengthSeconds) reasons.Add("trace_length_below_minimum"); if (feature.SampleRate < minSampleRate) reasons.Add("sample_rate_below_minimum"); if (feature.ChargeCount < minChargeCount) reasons.Add("charge_count_below_minimum"); if (feature.FeatureScore < minFeatureScore) reasons.Add("feature_score_below_minimum");
            feature.Accepted = reasons.Count == 0; feature.RejectReason = string.Join(";", reasons.ToArray()); feature.PreferredMass = feature.Mass; feature.RawMergedMassMin = feature.Mass; feature.RawMergedMassMax = feature.Mass; feature.RawMergedMassSpanDa = 0.0; feature.IsotopeAnchorAmbiguous = false; feature.MassInterpretation = "single accepted feature"; return feature;
        }

        private static List<FeatureGroup> CollapseIsotopeShiftedFeatures(List<FeatureGroup> features, double ppmTolerance, int isotopeCollapseMaxShift, double collapseApexToleranceMin, double collapseRtOverlapFraction)
        { List<FeatureGroup> sorted = features.OrderByDescending(f => f.SumIntensity).ToList(); bool[] used = new bool[sorted.Count]; List<FeatureGroup> collapsed = new List<FeatureGroup>(); for (int i = 0; i < sorted.Count; i++) { if (used[i]) continue; FeatureGroup representative = sorted[i]; List<FeatureGroup> cluster = new List<FeatureGroup>(); List<int> offsets = new List<int>(); cluster.Add(representative); offsets.Add(0); used[i] = true; for (int j = i + 1; j < sorted.Count; j++) { if (used[j]) continue; int offset; if (AreIsotopeShiftedDuplicates(representative, sorted[j], ppmTolerance, isotopeCollapseMaxShift, collapseApexToleranceMin, collapseRtOverlapFraction, out offset)) { cluster.Add(sorted[j]); offsets.Add(offset); used[j] = true; } } collapsed.Add(MergeFeatureCluster(representative, cluster, offsets, false)); } return collapsed; }
        private static bool AreIsotopeShiftedDuplicates(FeatureGroup a, FeatureGroup b, double ppmTolerance, int isotopeCollapseMaxShift, double collapseApexToleranceMin, double collapseRtOverlapFraction, out int isotopeOffset)
        { isotopeOffset = 0; if (isotopeCollapseMaxShift <= 0) return false; double diff = b.Mass - a.Mass; int n = (int)Math.Round(diff / C13MinusC12); if (n == 0) return false; if (Math.Abs(n) > isotopeCollapseMaxShift) return false; double residual = Math.Abs(diff - n * C13MinusC12), residualToleranceDa = Math.Max(0.05, a.Mass * ppmTolerance / 1e6); if (residual > residualToleranceDa) return false; if (!ChargeRangesOverlap(a, b)) return false; double rtOverlap = RetentionTimeOverlapFraction(a, b); if (rtOverlap < collapseRtOverlapFraction) return false; bool containedFragment = IsContainedRtFragment(a, b); if (!containedFragment && Math.Abs(a.ApexRt - b.ApexRt) > collapseApexToleranceMin) return false; isotopeOffset = n; return true; }
        private static bool IsContainedRtFragment(FeatureGroup a, FeatureGroup b) { FeatureGroup larger = a.TraceLengthSeconds >= b.TraceLengthSeconds ? a : b, smaller = a.TraceLengthSeconds < b.TraceLengthSeconds ? a : b; if (larger.TraceLengthSeconds <= 0.0 || smaller.TraceLengthSeconds <= 0.0) return false; bool smallerInside = smaller.StartRt >= larger.StartRt && smaller.EndRt <= larger.EndRt, muchShorter = smaller.TraceLengthSeconds <= larger.TraceLengthSeconds * 0.25, strongParent = larger.SumIntensity >= smaller.SumIntensity * 10.0; return smallerInside && muchShorter && strongParent; }
        private static List<FeatureGroup> CollapseWeakSameMassFragments(List<FeatureGroup> features, double ppmTolerance, double sameMassRtGapMin, double weakSameMassRelativeIntensity)
        { List<FeatureGroup> sorted = features.OrderByDescending(f => f.SumIntensity).ToList(); bool[] used = new bool[sorted.Count]; List<FeatureGroup> collapsed = new List<FeatureGroup>(); for (int i = 0; i < sorted.Count; i++) { if (used[i]) continue; FeatureGroup representative = sorted[i]; List<FeatureGroup> cluster = new List<FeatureGroup>(); List<int> offsets = new List<int>(); cluster.Add(representative); offsets.Add(0); used[i] = true; for (int j = i + 1; j < sorted.Count; j++) { if (used[j]) continue; if (AreWeakSameMassFragments(representative, sorted[j], ppmTolerance, sameMassRtGapMin, weakSameMassRelativeIntensity)) { cluster.Add(sorted[j]); offsets.Add(0); used[j] = true; } } collapsed.Add(MergeFeatureCluster(representative, cluster, offsets, true)); } return collapsed; }
        private static bool AreWeakSameMassFragments(FeatureGroup parent, FeatureGroup candidate, double ppmTolerance, double sameMassRtGapMin, double weakSameMassRelativeIntensity) { double ppm = Math.Abs(PpmError(candidate.Mass, parent.Mass)); if (double.IsNaN(ppm) || ppm > ppmTolerance) return false; if (!ChargeRangesOverlap(parent, candidate)) return false; double intensityRatio = parent.SumIntensity > 0.0 ? candidate.SumIntensity / parent.SumIntensity : 1.0; if (intensityRatio > weakSameMassRelativeIntensity) return false; double gap = RetentionTimeGap(parent, candidate); if (gap > sameMassRtGapMin) return false; return true; }

        private static FeatureGroup MergeFeatureCluster(FeatureGroup representative, List<FeatureGroup> cluster, List<int> isotopeOffsets, bool sameMassMerge)
        {
            if (cluster.Count == 1) { representative.MergedFeatureCount = Math.Max(1, representative.MergedFeatureCount); if (string.IsNullOrEmpty(representative.MergedIsotopeOffsets)) representative.MergedIsotopeOffsets = "0"; representative.RepresentativeFeatureIndex = representative.FeatureIndex; representative.PreferredMass = representative.Mass; representative.RawMergedMassMin = representative.Mass; representative.RawMergedMassMax = representative.Mass; representative.RawMergedMassSpanDa = 0.0; representative.IsotopeAnchorAmbiguous = false; representative.MassInterpretation = "single accepted feature"; return representative; }
            FeatureGroup merged = new FeatureGroup(); merged.Accepted = true; merged.RejectReason = ""; merged.RepresentativeFeatureIndex = representative.RepresentativeFeatureIndex > 0 ? representative.RepresentativeFeatureIndex : representative.FeatureIndex;
            double weightSum = 0.0, weightedMass = 0.0; int mergedCount = 0; List<string> mergedOffsetStrings = new List<string>(); List<double> rawMasses = new List<double>();
            for (int i = 0; i < cluster.Count; i++) { FeatureGroup f = cluster[i]; int offset = isotopeOffsets[i]; double rawMass = f.Mass, adjustedMass = sameMassMerge ? f.Mass : f.Mass - offset * C13MinusC12, weight = Math.Max(1.0, f.SumIntensity); rawMasses.Add(rawMass); weightedMass += adjustedMass * weight; weightSum += weight; mergedCount += Math.Max(1, f.MergedFeatureCount); if (!string.IsNullOrEmpty(f.MergedIsotopeOffsets) && f.MergedIsotopeOffsets != "0") mergedOffsetStrings.Add(f.MergedIsotopeOffsets); else mergedOffsetStrings.Add(offset.ToString(CultureInfo.InvariantCulture)); }
            merged.Mass = weightSum > 0.0 ? weightedMass / weightSum : representative.Mass; merged.SumIntensity = cluster.Max(f => f.SumIntensity); merged.MaxScanIntensity = cluster.Max(f => f.MaxScanIntensity); merged.StartRt = cluster.Min(f => f.StartRt); merged.EndRt = cluster.Max(f => f.EndRt); merged.ApexRt = cluster.OrderByDescending(f => f.MaxScanIntensity).First().ApexRt; merged.TraceLengthSeconds = Math.Max(0.0, (merged.EndRt - merged.StartRt) * 60.0); merged.Scans = cluster.SelectMany(f => f.Scans).ToList(); merged.MinCharge = cluster.Min(f => f.MinCharge); merged.MaxCharge = cluster.Max(f => f.MaxCharge); merged.ChargeCount = CountDistinctChargesFromFeatures(cluster); merged.ChargeContinuity = merged.MaxCharge >= merged.MinCharge ? merged.ChargeCount / (double)(merged.MaxCharge - merged.MinCharge + 1) : 1.0;
            List<int> scanNumbers = merged.Scans.Select(s => s.Scan).Distinct().OrderBy(s => s).ToList(); if (scanNumbers.Count > 0) { merged.MatchedScans = scanNumbers.Count; merged.ScanSpan = scanNumbers.Last() - scanNumbers.First() + 1; merged.SampleRate = merged.ScanSpan > 0 ? merged.MatchedScans / (double)merged.ScanSpan : 1.0; } else { merged.MatchedScans = cluster.Sum(f => f.MatchedScans); merged.ScanSpan = cluster.Max(f => f.ScanSpan); merged.SampleRate = Math.Min(1.0, merged.MatchedScans / (double)Math.Max(1, merged.ScanSpan)); }
            FeatureGroup bestScore = cluster.OrderByDescending(f => f.FeatureScore).First(); merged.MedianPpmError = bestScore.MedianPpmError; merged.PpmMad = cluster.Max(f => f.PpmMad); merged.MedianIsotopeCosine = WeightedAverageFeatureValue(cluster, f => f.MedianIsotopeCosine); merged.BestIsotopeCosine = cluster.Max(f => f.BestIsotopeCosine); merged.MedianMatchedIsotopes = (int)Math.Round(WeightedAverageFeatureValue(cluster, f => (double)f.MedianMatchedIsotopes)); merged.ApexDominance = merged.SumIntensity > 0.0 ? merged.MaxScanIntensity / merged.SumIntensity : 0.0; merged.FeatureScore = cluster.Max(f => f.FeatureScore); merged.MergedFeatureCount = mergedCount; merged.MergedIsotopeOffsets = string.Join(";", mergedOffsetStrings.ToArray());
            merged.RawMergedMassMin = rawMasses.Min(); merged.RawMergedMassMax = rawMasses.Max(); merged.RawMergedMassSpanDa = merged.RawMergedMassMax - merged.RawMergedMassMin;
            bool hasNonZeroOffset = isotopeOffsets.Any(x => x != 0), weakFeature = merged.SumIntensity < 1.0e10, smallMerge = cluster.Count <= 3, oneIsotopeSpan = merged.RawMergedMassSpanDa > 0.75 && merged.RawMergedMassSpanDa < 1.25;
            if (!sameMassMerge && hasNonZeroOffset && weakFeature && smallMerge && oneIsotopeSpan) { merged.IsotopeAnchorAmbiguous = true; merged.PreferredMass = merged.RawMergedMassMin; merged.MassInterpretation = "weak isotope-anchor ambiguous feature; preferred mass is lowest merged raw mass"; } else { merged.IsotopeAnchorAmbiguous = false; merged.PreferredMass = merged.Mass; merged.MassInterpretation = !sameMassMerge ? "isotope-offset collapsed feature" : "same-mass weak RT fragments collapsed into dominant feature"; }
            return merged;
        }

        private static double WeightedAverageFeatureValue(List<FeatureGroup> cluster, Func<FeatureGroup, double> selector) { double weightSum = 0.0, valueSum = 0.0; foreach (FeatureGroup f in cluster) { double v = selector(f); if (double.IsNaN(v)) continue; double w = Math.Max(1.0, f.SumIntensity); valueSum += v * w; weightSum += w; } return weightSum <= 0.0 ? double.NaN : valueSum / weightSum; }
        private static int CountDistinctChargesFromFeatures(List<FeatureGroup> features) { HashSet<int> charges = new HashSet<int>(); foreach (FeatureGroup f in features) for (int z = f.MinCharge; z <= f.MaxCharge; z++) charges.Add(z); return charges.Count; }
        private static double RetentionTimeOverlapFraction(FeatureGroup a, FeatureGroup b) { double left = Math.Max(a.StartRt, b.StartRt), right = Math.Min(a.EndRt, b.EndRt); if (right < left) return 0.0; double intersection = right - left, lenA = Math.Max(0.000001, a.EndRt - a.StartRt), lenB = Math.Max(0.000001, b.EndRt - b.StartRt), shorter = Math.Min(lenA, lenB); return intersection / shorter; }
        private static double RetentionTimeGap(FeatureGroup a, FeatureGroup b) { if (b.EndRt < a.StartRt) return a.StartRt - b.EndRt; if (a.EndRt < b.StartRt) return b.StartRt - a.EndRt; return 0.0; }
        private static bool ChargeRangesOverlap(FeatureGroup a, FeatureGroup b) { int left = Math.Max(a.MinCharge, b.MinCharge), right = Math.Min(a.MaxCharge, b.MaxCharge); return left <= right; }
        private static int CountDistinctCharges(List<ScanSummary> scans) { HashSet<int> charges = new HashSet<int>(); foreach (ScanSummary s in scans) for (int z = s.MinCharge; z <= s.MaxCharge; z++) charges.Add(z); return charges.Count; }
        private static double CalculateFeatureScore(FeatureGroup f) { double isotope = Clamp01((f.MedianIsotopeCosine - 0.70) / 0.30), charge = Clamp01(f.ChargeContinuity) * Clamp01(f.ChargeCount / 6.0), scans = Clamp01(f.MatchedScans / 8.0), sample = Clamp01(f.SampleRate), massAccuracy = Clamp01(1.0 - Math.Abs(f.MedianPpmError) / 20.0), apex = Clamp01(f.ApexDominance * 5.0); return 0.35 * isotope + 0.20 * charge + 0.15 * scans + 0.15 * sample + 0.10 * massAccuracy + 0.05 * apex; }
        private static double Clamp01(double x) { if (double.IsNaN(x)) return 0.0; if (x < 0.0) return 0.0; if (x > 1.0) return 1.0; return x; }
        private static double WeightedMeanMassFromScans(List<ScanSummary> scans) { double total = scans.Sum(s => s.ScanEnvelopeIntensity); return total <= 0.0 ? scans.Average(s => s.Mass) : scans.Sum(s => s.Mass * s.ScanEnvelopeIntensity) / total; }

        private static void WriteEvidence(string path, List<EnvelopeHit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tCandidateMass\tObservedMass\tPpmError\tCharge\tSeedIsotopeIndex\tBaseMz\tEnvelopeIntensity\tMaxIsotopeIntensity\tApexIsotopeIndex\tMatchedIsotopeCount\tTotalIsotopeCount\tIsotopeCosineScore\tBasePeakMass\tTIC");
                foreach (EnvelopeHit h in hits.OrderBy(h => h.ObservedMass).ThenBy(h => h.Scan).ThenBy(h => h.Charge)) writer.WriteLine(h.Scan.ToString(CultureInfo.InvariantCulture) + "\t" + h.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" + h.CandidateMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" + h.ObservedMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" + h.PpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" + h.Charge.ToString(CultureInfo.InvariantCulture) + "\t" + h.SeedIsotopeIndex.ToString(CultureInfo.InvariantCulture) + "\t" + h.BaseMz.ToString("F6", CultureInfo.InvariantCulture) + "\t" + h.EnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" + h.MaxIsotopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" + h.ApexIsotopeIndex.ToString(CultureInfo.InvariantCulture) + "\t" + h.MatchedIsotopeCount.ToString(CultureInfo.InvariantCulture) + "\t" + h.TotalIsotopeCount.ToString(CultureInfo.InvariantCulture) + "\t" + h.IsotopeCosineScore.ToString("F6", CultureInfo.InvariantCulture) + "\t" + h.BasePeakMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" + h.Tic.ToString("G17", CultureInfo.InvariantCulture));
            }
        }
        private static void WriteScanSummary(string path, List<ScanSummary> scanSummaries)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tMass\tMedianPpmError\tScanEnvelopeIntensity\tMaxChargeEnvelopeIntensity\tBestCharge\tBestChargeCosine\tMedianIsotopeCosine\tMedianMatchedIsotopes\tMinCharge\tMaxCharge\tChargeCount\tChargeContinuity\tEvidenceCount");
                foreach (ScanSummary s in scanSummaries.OrderBy(s => s.Mass).ThenBy(s => s.Scan)) writer.WriteLine(s.Scan.ToString(CultureInfo.InvariantCulture) + "\t" + s.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" + s.Mass.ToString("F6", CultureInfo.InvariantCulture) + "\t" + s.MedianPpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" + s.ScanEnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" + s.MaxChargeEnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" + s.BestCharge.ToString(CultureInfo.InvariantCulture) + "\t" + s.BestChargeCosine.ToString("F6", CultureInfo.InvariantCulture) + "\t" + s.MedianIsotopeCosine.ToString("F6", CultureInfo.InvariantCulture) + "\t" + s.MedianMatchedIsotopes.ToString(CultureInfo.InvariantCulture) + "\t" + s.MinCharge.ToString(CultureInfo.InvariantCulture) + "\t" + s.MaxCharge.ToString(CultureInfo.InvariantCulture) + "\t" + s.ChargeCount.ToString(CultureInfo.InvariantCulture) + "\t" + s.ChargeContinuity.ToString("F6", CultureInfo.InvariantCulture) + "\t" + s.EvidenceCount.ToString(CultureInfo.InvariantCulture));
            }
        }
        private static void WriteFeatureSummary(string path, List<FeatureGroup> features, bool rejected)
        {
            using (var writer = new StreamWriter(path))
            {
                string header = "FeatureIndex\tReportedMonoisotopicMass\tPreferredMonoisotopicMass\tRawMergedMassMin\tRawMergedMassMax\tRawMergedMassSpanDa\tIsotopeAnchorAmbiguous\tMassInterpretation\tStartRetentionTime\tEndRetentionTime\tApexRetentionTime\tTraceLengthSeconds\tSumIntensity\tMaxScanIntensity\tMinCharge\tMaxCharge\tChargeCount\tChargeContinuity\tMatchedScans\tScanSpan\tSampleRate\tMedianPpmError\tPpmMAD\tMedianIsotopeCosineScore\tBestIsotopeCosineScore\tMedianMatchedIsotopes\tApexDominance\tFeatureScore\tMergedFeatureCount\tMergedIsotopeOffsets\tRepresentativeFeatureIndex";
                if (rejected) header += "\tRejectReason"; writer.WriteLine(header);
                foreach (FeatureGroup f in features)
                {
                    if (f.PreferredMass == 0.0) f.PreferredMass = f.Mass; if (f.RawMergedMassMin == 0.0 && f.RawMergedMassMax == 0.0) { f.RawMergedMassMin = f.Mass; f.RawMergedMassMax = f.Mass; f.RawMergedMassSpanDa = 0.0; } if (string.IsNullOrEmpty(f.MassInterpretation)) f.MassInterpretation = "accepted feature";
                    string line = f.FeatureIndex.ToString(CultureInfo.InvariantCulture) + "\t" + f.Mass.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.PreferredMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.RawMergedMassMin.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.RawMergedMassMax.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.RawMergedMassSpanDa.ToString("F6", CultureInfo.InvariantCulture) + "\t" + (f.IsotopeAnchorAmbiguous ? "1" : "0") + "\t" + SafeTsv(f.MassInterpretation) + "\t" + f.StartRt.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.EndRt.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.ApexRt.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.TraceLengthSeconds.ToString("F3", CultureInfo.InvariantCulture) + "\t" + f.SumIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" + f.MaxScanIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" + f.MinCharge.ToString(CultureInfo.InvariantCulture) + "\t" + f.MaxCharge.ToString(CultureInfo.InvariantCulture) + "\t" + f.ChargeCount.ToString(CultureInfo.InvariantCulture) + "\t" + f.ChargeContinuity.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.MatchedScans.ToString(CultureInfo.InvariantCulture) + "\t" + f.ScanSpan.ToString(CultureInfo.InvariantCulture) + "\t" + f.SampleRate.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.MedianPpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" + f.PpmMad.ToString("F4", CultureInfo.InvariantCulture) + "\t" + f.MedianIsotopeCosine.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.BestIsotopeCosine.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.MedianMatchedIsotopes.ToString(CultureInfo.InvariantCulture) + "\t" + f.ApexDominance.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.FeatureScore.ToString("F6", CultureInfo.InvariantCulture) + "\t" + f.MergedFeatureCount.ToString(CultureInfo.InvariantCulture) + "\t" + SafeTsv(f.MergedIsotopeOffsets) + "\t" + f.RepresentativeFeatureIndex.ToString(CultureInfo.InvariantCulture);
                    if (rejected) line += "\t" + SafeTsv(f.RejectReason); writer.WriteLine(line);
                }
            }
        }

        private static string SafeTsv(string value) { if (value == null) return ""; return value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }
        private static double Cosine(double[] observed, double[] theoretical) { double dot = 0.0, a = 0.0, b = 0.0; int n = Math.Min(observed.Length, theoretical.Length); for (int i = 0; i < n; i++) { dot += observed[i] * theoretical[i]; a += observed[i] * observed[i]; b += theoretical[i] * theoretical[i]; } if (a <= 0.0 || b <= 0.0) return 0.0; return dot / Math.Sqrt(a * b); }
        private static double PpmError(double observed, double expected) { if (expected == 0.0 || double.IsNaN(observed) || double.IsNaN(expected)) return double.NaN; return (observed - expected) / expected * 1e6; }
        private static double Median(List<double> values) { List<double> clean = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList(); if (clean.Count == 0) return double.NaN; int mid = clean.Count / 2; if (clean.Count % 2 == 1) return clean[mid]; return 0.5 * (clean[mid - 1] + clean[mid]); }
        private static double MedianAbsoluteDeviation(List<double> values) { List<double> clean = values.Where(v => !double.IsNaN(v)).ToList(); if (clean.Count == 0) return double.NaN; double med = Median(clean); return Median(clean.Select(v => Math.Abs(v - med)).ToList()); }
        private static void ReportProgress() { int done = Interlocked.Increment(ref ProgressDone); int percent = (int)((done * 100L) / Math.Max(1, ProgressTotal)); if (percent != ProgressLastPercent) { lock (ProgressLock) { if (percent != ProgressLastPercent) { ProgressLastPercent = percent; PrintProgress(percent); } } } }
        private static void PrintProgress(int percent) { int barWidth = 10, filled = (percent * barWidth) / 100; Console.Write("\rProgress: [" + new string('#', filled) + new string('-', barWidth - filled) + "] " + percent.ToString().PadLeft(3) + "%"); }
    }
}
