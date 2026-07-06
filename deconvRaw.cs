// compile: mcs deconvRaw.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:deconvRaw.exe
// run IgG/IgL 18-25 kDa discovery: mono deconvRaw.exe 260629_Solveig_3_IgG.raw 18000 25000 8 40 10 1000000 10000000 0.85 5 3 2 35 12 0 -1 -1 -1 -1 0 0.05 3 0.20 3 300 8 20.0 0.70 1.0 0.01 > log.txt 2>&1
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
        private static readonly ConcurrentDictionary<int, double[]> AveragineCache = new ConcurrentDictionary<int, double[]>();
        private static int ProgressTotal = 0;
        private static int ProgressDone = 0;
        private static int ProgressLastPercent = -1;
        private static readonly object ProgressLock = new object();

        private class ScanRange { public int Start; public int End; }
        private class PeakHit { public int Index; public double Mz; public double Intensity; }
        private class WorkerResult { public List<ScanSummary> Summaries = new List<ScanSummary>(); public List<EnvelopeHit> Evidence = new List<EnvelopeHit>(); }

        private class EnvelopeHit
        {
            public int Scan; public double Rt; public double CandidateMass; public double ObservedMass; public double PpmError; public int Charge; public int SeedIsotopeIndex;
            public double BaseMz; public double EnvelopeIntensity; public double MaxIsotopeIntensity; public int ApexIsotopeIndex; public int MatchedIsotopeCount; public int TotalIsotopeCount;
            public double IsotopeCosineScore; public double BasePeakMass; public double Tic;
        }

        private class ScanSummary
        {
            public int Scan; public double Rt; public double Mass; public double MedianPpmError; public double WeightedAbsPpmError; public double ScanEnvelopeIntensity; public double MaxChargeEnvelopeIntensity;
            public int BestCharge; public double BestChargeCosine; public double MedianIsotopeCosine; public int MedianMatchedIsotopes;
            public int BestApexIsotopeIndex; public int ExpectedApexIsotopeIndex; public double ApexIsotopeDelta;
            public int MinCharge; public int MaxCharge; public int ChargeCount; public double ChargeContinuity; public int EvidenceCount;
        }

        private class AnchorEvidence
        {
            public double Mass; public double SumIntensity; public double TraceLengthSeconds; public int MatchedScans; public double SampleRate;
            public int ChargeCount; public double MedianIsotopeCosine; public double BestIsotopeCosine; public double FeatureScore;
            public double StartRt; public double EndRt; public double ApexRt; public int FeatureIndex;
            public double MedianPpmError; public double WeightedAbsPpmError;
            public int ObservedApexIsotopeIndex; public int ExpectedApexIsotopeIndex; public double ApexIsotopeDelta;
        }

        private class FeatureGroup
        {
            public int FeatureIndex; public int RepresentativeFeatureIndex; public int MergedFeatureCount; public string MergedIsotopeOffsets;
            public double Mass; public double PreferredMass; public double RawMergedMassMin; public double RawMergedMassMax; public double RawMergedMassSpanDa;
            public bool IsotopeAnchorAmbiguous; public string MassInterpretation; public List<AnchorEvidence> RawAnchors = new List<AnchorEvidence>();
            public List<ScanSummary> Scans = new List<ScanSummary>(); public bool Accepted; public string RejectReason;
            public double StartRt; public double EndRt; public double ApexRt; public double TraceLengthSeconds; public double SumIntensity; public double MaxScanIntensity;
            public int MinCharge; public int MaxCharge; public int ChargeCount; public double ChargeContinuity; public int MatchedScans; public int ScanSpan; public double SampleRate;
            public double MedianPpmError; public double WeightedAbsPpmError; public double PpmMad; public double MedianIsotopeCosine; public double BestIsotopeCosine; public int MedianMatchedIsotopes;
            public int ObservedApexIsotopeIndex; public int ExpectedApexIsotopeIndex; public double ApexIsotopeDelta;
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
            double minMass = ParseD(args, 1, 10000.0), maxMass = ParseD(args, 2, 100000.0), ppmTolerance = ParseD(args, 5, 10.0), minSeedIntensity = ParseD(args, 6, 1000000.0), minEnvelopeIntensity = ParseD(args, 7, 10000000.0), minCos = ParseD(args, 8, 0.85);
            int minCharge = ParseI(args, 3, 1), maxCharge = ParseI(args, 4, 80), minMatchedIsotopes = ParseI(args, 9, 5), minFeatureScans = ParseI(args, 10, 3), maxGapScans = ParseI(args, 11, 2), maxSeedIso = ParseI(args, 12, 45), threads = ParseI(args, 13, 0), writeEvidenceInt = ParseI(args, 14, 0);
            double minRt = ParseD(args, 15, -1.0), maxRt = ParseD(args, 16, -1.0), minMz = ParseD(args, 17, -1.0), maxMz = ParseD(args, 18, -1.0), minTraceSeconds = ParseD(args, 19, 0.0), minSampleRate = ParseD(args, 20, 0.05), minFeatureScore = ParseD(args, 22, 0.0);
            int minChargeCount = ParseI(args, 21, 1), seedIsoWindow = ParseI(args, 23, 3), maxSeedPeaks = ParseI(args, 24, 300), isotopeCollapseMaxShift = ParseI(args, 25, 8);
            double collapseApexToleranceMin = ParseD(args, 26, 20.0), collapseRtOverlapFraction = ParseD(args, 27, 0.70), sameMassRtGapMin = ParseD(args, 28, 1.0), weakSameMassRelativeIntensity = ParseD(args, 29, 0.01);

            if (minCharge < 1) minCharge = 1; if (maxCharge < minCharge) maxCharge = minCharge; if (threads <= 0) threads = Environment.ProcessorCount; if (threads < 1) threads = 1;
            if (seedIsoWindow < 0) seedIsoWindow = -1; if (maxSeedPeaks < 0) maxSeedPeaks = 0; if (isotopeCollapseMaxShift < 0) isotopeCollapseMaxShift = 0;
            if (minSampleRate < 0.0) minSampleRate = 0.0; if (minSampleRate > 1.0) minSampleRate = 1.0; if (minChargeCount < 1) minChargeCount = 1;
            if (collapseRtOverlapFraction < 0.0) collapseRtOverlapFraction = 0.0; if (collapseRtOverlapFraction > 1.0) collapseRtOverlapFraction = 1.0;
            bool writeEvidence = writeEvidenceInt != 0;

            var rawFile = RawFileReaderAdapter.FileFactory(rawPath);
            if (!rawFile.IsOpen || rawFile.IsError) { Console.Error.WriteLine("Error opening raw file: {0} FileError: {1}", rawPath, rawFile.FileError); return; }
            rawFile.SelectInstrument(Device.MS, 1);
            int firstScan = rawFile.RunHeaderEx.FirstSpectrum, lastScan = rawFile.RunHeaderEx.LastSpectrum, scanCount = lastScan - firstScan + 1;

            PrintParam("filename", rawFile.FileName); PrintParam("scans", scanCount); PrintParam("minMass", minMass); PrintParam("maxMass", maxMass); PrintParam("minCharge", minCharge); PrintParam("maxCharge", maxCharge);
            PrintParam("ppmTolerance", ppmTolerance); PrintParam("minSeedIntensity", minSeedIntensity); PrintParam("minEnvelopeIntensity", minEnvelopeIntensity); PrintParam("minCos", minCos); PrintParam("minMatchedIsotopes", minMatchedIsotopes);
            PrintParam("minFeatureScans", minFeatureScans); PrintParam("maxGapScans", maxGapScans); PrintParam("maxSeedIsotopeIndex", maxSeedIso); PrintParam("threads", threads); PrintParam("writeEvidence", writeEvidence ? 1 : 0);
            PrintParam("minRt", minRt); PrintParam("maxRt", maxRt); PrintParam("minMz", minMz); PrintParam("maxMz", maxMz); PrintParam("minTraceLengthSeconds", minTraceSeconds); PrintParam("minSampleRate", minSampleRate);
            PrintParam("minChargeCount", minChargeCount); PrintParam("minFeatureScore", minFeatureScore); PrintParam("seedIsoWindow", seedIsoWindow); PrintParam("maxSeedPeaks", maxSeedPeaks); PrintParam("isotopeCollapseMaxShift", isotopeCollapseMaxShift);
            PrintParam("collapseApexToleranceMin", collapseApexToleranceMin); PrintParam("collapseRtOverlapFraction", collapseRtOverlapFraction); PrintParam("sameMassRtGapMin", sameMassRtGapMin); PrintParam("weakSameMassRelativeIntensity", weakSameMassRelativeIntensity);
            rawFile.Dispose();

            ProgressTotal = scanCount; ProgressDone = 0; ProgressLastPercent = -1;
            List<ScanRange> ranges = BuildScanRanges(firstScan, lastScan, Math.Max(10, scanCount / Math.Max(1, threads * 8)));
            var bag = new ConcurrentBag<WorkerResult>();
            Parallel.ForEach(ranges, new ParallelOptions { MaxDegreeOfParallelism = threads }, range =>
                bag.Add(ProcessScanRange(rawPath, range.Start, range.End, minMass, maxMass, minCharge, maxCharge, ppmTolerance, minSeedIntensity, minEnvelopeIntensity, minCos, minMatchedIsotopes, maxSeedIso, seedIsoWindow, maxSeedPeaks, writeEvidence, minRt, maxRt, minMz, maxMz)));
            Console.WriteLine();

            List<ScanSummary> summaries = bag.SelectMany(x => x.Summaries).OrderBy(x => x.Mass).ThenBy(x => x.Scan).ToList();
            List<EnvelopeHit> evidence = writeEvidence ? bag.SelectMany(x => x.Evidence).ToList() : new List<EnvelopeHit>();
            List<FeatureGroup> features = BuildGlobalFeatures(summaries, ppmTolerance, minFeatureScans, maxGapScans, minTraceSeconds, minSampleRate, minChargeCount, minFeatureScore);
            List<FeatureGroup> accepted = features.Where(f => f.Accepted).OrderByDescending(f => f.SumIntensity).ToList();
            int idx = 1;
            foreach (FeatureGroup f in accepted) { f.FeatureIndex = idx; f.RepresentativeFeatureIndex = idx; f.MergedFeatureCount = 1; f.MergedIsotopeOffsets = "0"; foreach (AnchorEvidence a in f.RawAnchors) a.FeatureIndex = idx; idx++; }
            List<FeatureGroup> rejected = features.Where(f => !f.Accepted).OrderByDescending(f => f.SumIntensity).ToList();
            idx = 1;
            foreach (FeatureGroup f in rejected) { f.FeatureIndex = idx; f.RepresentativeFeatureIndex = idx; f.MergedFeatureCount = 1; f.MergedIsotopeOffsets = "0"; foreach (AnchorEvidence a in f.RawAnchors) a.FeatureIndex = idx; idx++; }

            string prefix = rawPath + ".discovery";
            WriteFeatureSummary(prefix + ".deconv_masses.uncollapsed.tsv", accepted, false);
            List<FeatureGroup> collapsed = CollapseIsotopeShiftedFeatures(accepted, ppmTolerance, isotopeCollapseMaxShift, collapseApexToleranceMin, collapseRtOverlapFraction);
            collapsed = CollapseWeakSameMassFragments(collapsed, ppmTolerance, sameMassRtGapMin, weakSameMassRelativeIntensity);
            idx = 1; foreach (FeatureGroup f in collapsed.OrderByDescending(f => f.SumIntensity)) f.FeatureIndex = idx++;
            WriteScanSummary(prefix + ".scan_summary.tsv", summaries);
            WriteFeatureSummary(prefix + ".deconv_masses.tsv", collapsed.OrderByDescending(f => f.SumIntensity).ToList(), false);
            WriteFeatureSummary(prefix + ".rejected_masses.tsv", rejected, true);
            if (writeEvidence) { WriteEvidence(prefix + ".scan_evidence.tsv", evidence); Console.WriteLine("Wrote scan evidence: {0}", prefix + ".scan_evidence.tsv"); } else Console.WriteLine("Skipped scan evidence output. Set writeEvidence=1 to enable.");
            Console.WriteLine("Wrote scan summary: {0}", prefix + ".scan_summary.tsv");
            Console.WriteLine("Wrote final isotope/same-mass collapsed deconvolved masses: {0}", prefix + ".deconv_masses.tsv");
            Console.WriteLine("Wrote uncollapsed accepted masses: {0}", prefix + ".deconv_masses.uncollapsed.tsv");
            Console.WriteLine("Wrote rejected masses: {0}", prefix + ".rejected_masses.tsv");
        }

        private static int ParseI(string[] a, int i, int d) { int v; return a.Length > i && int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d; }
        private static double ParseD(string[] a, int i, double d) { double v; return a.Length > i && double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d; }
        private static void PrintParam(string name, object value) { Console.WriteLine("#{0}:\t{1}", name, Convert.ToString(value, CultureInfo.InvariantCulture)); }
        private static List<ScanRange> BuildScanRanges(int first, int last, int chunk) { var r = new List<ScanRange>(); for (int s = first; s <= last; s += chunk) r.Add(new ScanRange { Start = s, End = Math.Min(last, s + chunk - 1) }); return r; }

        private static WorkerResult ProcessScanRange(string rawPath, int startScan, int endScan, double minMass, double maxMass, int minCharge, int maxCharge, double ppm, double minSeed, double minEnv, double minCos, int minIso, int maxSeedIso, int seedIsoWindow, int maxSeedPeaks, bool writeEvidence, double minRt, double maxRt, double minMz, double maxMz)
        {
            WorkerResult result = new WorkerResult();
            var raw = RawFileReaderAdapter.FileFactory(rawPath);
            if (!raw.IsOpen || raw.IsError) return result;
            raw.SelectInstrument(Device.MS, 1);
            try
            {
                for (int scan = startScan; scan <= endScan; scan++)
                {
                    try
                    {
                        double rt = raw.RetentionTimeFromScanNumber(scan);
                        if (minRt >= 0.0 && rt < minRt) { ReportProgress(); continue; }
                        if (maxRt >= 0.0 && rt > maxRt) { ReportProgress(); continue; }
                        string ev = ""; try { ev = string.Join(" ", raw.GetScanEventForScanNumber(scan)); } catch { }
                        if (ev.Length > 0 && (ev.IndexOf("ms", StringComparison.OrdinalIgnoreCase) < 0 || ev.IndexOf("@", StringComparison.OrdinalIgnoreCase) >= 0)) { ReportProgress(); continue; }
                        CentroidStream cs = null; try { cs = raw.GetCentroidStream(scan, false); } catch { }
                        if (cs == null || cs.Length <= 0) { ReportProgress(); continue; }
                        List<EnvelopeHit> hits = DiscoverScanMasses(scan, rt, raw.GetScanStatsForScanNumber(scan), cs, minMass, maxMass, minCharge, maxCharge, ppm, minSeed, minEnv, minCos, minIso, maxSeedIso, seedIsoWindow, maxSeedPeaks, minMz, maxMz);
                        if (hits.Count > 0) { result.Summaries.AddRange(CollapseScanHits(hits, ppm)); if (writeEvidence) result.Evidence.AddRange(hits); }
                    }
                    catch (Exception ex) { Console.Error.WriteLine("Warning: failed scan {0}: {1}", scan, ex.Message); }
                    ReportProgress();
                }
            }
            finally { raw.Dispose(); }
            return result;
        }

        private static List<EnvelopeHit> DiscoverScanMasses(int scan, double rt, dynamic stats, CentroidStream cs, double minMass, double maxMass, int minCharge, int maxCharge, double ppm, double minSeed, double minEnv, double minCos, int minIso, int maxSeedIso, int seedIsoWindow, int maxSeedPeaks, double minMz, double maxMz)
        {
            var hits = new List<EnvelopeHit>();
            double[] mz = cs.Masses, inten = cs.Intensities, charges = null;
            try { charges = cs.Charges; } catch { }
            if (mz == null || inten == null || mz.Length != inten.Length) return hits;
            foreach (int pi in GetSeedPeakIndices(inten, minSeed, maxSeedPeaks))
            {
                double seedMz = mz[pi];
                if (minMz >= 0.0 && seedMz < minMz) continue;
                if (maxMz >= 0.0 && seedMz > maxMz) continue;
                foreach (int z in GetCandidateCharges(charges, pi, minCharge, maxCharge))
                {
                    int firstIso, lastIso;
                    GetSeedIsotopeRange(seedMz, z, minMass, maxMass, maxSeedIso, seedIsoWindow, out firstIso, out lastIso);
                    for (int si = firstIso; si <= lastIso; si++)
                    {
                        double cm = seedMz * z - z * Proton - si * C13MinusC12;
                        if (cm < minMass || cm > maxMass) continue;
                        EnvelopeHit h = TryMatchEnvelope(scan, rt, stats, cm, z, si, mz, inten, ppm, minEnv, minCos, minIso);
                        if (h != null) hits.Add(h);
                    }
                }
            }
            return hits;
        }

        private static List<int> GetSeedPeakIndices(double[] inten, double minSeed, int maxSeedPeaks) { var idx = new List<int>(); for (int i = 0; i < inten.Length; i++) if (inten[i] >= minSeed) idx.Add(i); idx = idx.OrderByDescending(i => inten[i]).ToList(); if (maxSeedPeaks > 0 && idx.Count > maxSeedPeaks) idx = idx.Take(maxSeedPeaks).ToList(); return idx; }
        private static void GetSeedIsotopeRange(double seedMz, int z, double minMass, double maxMass, int maxSeedIso, int seedIsoWindow, out int first, out int last) { if (seedIsoWindow < 0) { first = 0; last = maxSeedIso; return; } double m = seedMz * z - z * Proton; if (m < minMass) m = minMass; if (m > maxMass) m = maxMass; int apex = Math.Max(0, (int)Math.Round(m / 1800.0)); first = Math.Max(0, apex - seedIsoWindow); last = Math.Min(maxSeedIso, apex + seedIsoWindow); }
        private static List<int> GetCandidateCharges(double[] chargeArray, int peakIndex, int minCharge, int maxCharge) { var c = new List<int>(); int z = 0; try { if (chargeArray != null && peakIndex < chargeArray.Length) z = (int)Math.Round(chargeArray[peakIndex]); } catch { } if (z >= minCharge && z <= maxCharge) c.Add(z); else for (int i = minCharge; i <= maxCharge; i++) c.Add(i); return c; }

        private static EnvelopeHit TryMatchEnvelope(int scan, double rt, dynamic stats, double cm, int z, int seedIso, double[] mz, double[] inten, double ppm, double minEnv, double minCos, int minIso)
        {
            int n = EstimateIsotopeCount(cm);
            double[] theo = GetCachedAveragineEnvelope(cm, n), obs = new double[n];
            double env = 0.0, maxI = 0.0, wsum = 0.0, w = 0.0;
            int apex = -1, matched = 0;
            for (int i = 0; i < n; i++)
            {
                double emz = (cm + i * C13MinusC12 + z * Proton) / z;
                double tol = emz * ppm / 1e6;
                PeakHit p = FindHighestPeak(mz, inten, emz - tol, emz + tol);
                if (p != null)
                {
                    obs[i] = p.Intensity; env += p.Intensity; matched++;
                    if (p.Intensity > maxI) { maxI = p.Intensity; apex = i; }
                    double om = p.Mz * z - z * Proton - i * C13MinusC12;
                    wsum += om * p.Intensity; w += p.Intensity;
                }
            }
            if (matched < minIso || env < minEnv) return null;
            double cos = Cosine(obs, theo);
            if (cos < minCos) return null;
            double omass = w > 0.0 ? wsum / w : double.NaN;
            double bpm = double.NaN, tic = double.NaN;
            try { bpm = stats.BasePeakMass; tic = stats.TIC; } catch { }
            return new EnvelopeHit { Scan = scan, Rt = rt, CandidateMass = cm, ObservedMass = omass, PpmError = PpmError(omass, cm), Charge = z, SeedIsotopeIndex = seedIso, BaseMz = (cm + z * Proton) / z, EnvelopeIntensity = env, MaxIsotopeIntensity = maxI, ApexIsotopeIndex = apex, MatchedIsotopeCount = matched, TotalIsotopeCount = n, IsotopeCosineScore = cos, BasePeakMass = bpm, Tic = tic };
        }

        private static int EstimateIsotopeCount(double m) { double l = m / 1800.0; int n = (int)Math.Ceiling(l + 8.0 * Math.Sqrt(Math.Max(1.0, l)) + 12.0); if (n < 16) n = 16; if (n > 160) n = 160; return n; }
        private static int ExpectedApexIsotopeIndex(double mass) { return Math.Max(0, (int)Math.Round(mass / 1800.0)); }
        private static double[] GetCachedAveragineEnvelope(double m, int n) { int bin = (int)Math.Round(m / AveragineCacheBinDa); int key = n * 1000000 + bin; return AveragineCache.GetOrAdd(key, k => BuildAveragineEnvelope(bin * AveragineCacheBinDa, n)); }
        private static double[] BuildAveragineEnvelope(double m, int n) { double scale = m / 111.1254; double lambda = Math.Max(1.0, 4.9384 * scale) * 0.0107; double[] e = new double[n]; e[0] = Math.Exp(-lambda); for (int i = 1; i < n; i++) e[i] = e[i - 1] * lambda / i; Normalize(e); return e; }
        private static PeakHit FindHighestPeak(double[] mz, double[] inten, double lo, double hi) { int s = LowerBound(mz, lo); PeakHit best = null; for (int i = s; i < mz.Length && mz[i] <= hi; i++) if (best == null || inten[i] > best.Intensity) best = new PeakHit { Index = i, Mz = mz[i], Intensity = inten[i] }; return best; }
        private static int LowerBound(double[] a, double v) { int l = 0, r = a.Length; while (l < r) { int m = l + (r - l) / 2; if (a[m] < v) l = m + 1; else r = m; } return l; }

        private static List<ScanSummary> CollapseScanHits(List<EnvelopeHit> hits, double ppm)
        {
            var outp = new List<ScanSummary>();
            var sorted = hits.Where(h => !double.IsNaN(h.ObservedMass)).OrderBy(h => h.ObservedMass).ToList();
            var cur = new List<EnvelopeHit>(); double cen = double.NaN;
            foreach (EnvelopeHit h in sorted)
            {
                if (cur.Count == 0 || Math.Abs(PpmError(h.ObservedMass, cen)) <= ppm) { cur.Add(h); cen = WeightedMeanMass(cur); }
                else { outp.Add(MakeScanSummary(cur)); cur = new List<EnvelopeHit> { h }; cen = h.ObservedMass; }
            }
            if (cur.Count > 0) outp.Add(MakeScanSummary(cur));
            return outp;
        }

        private static ScanSummary MakeScanSummary(List<EnvelopeHit> hits)
        {
            var byz = new Dictionary<int, EnvelopeHit>();
            foreach (EnvelopeHit h in hits) if (!byz.ContainsKey(h.Charge) || h.EnvelopeIntensity > byz[h.Charge].EnvelopeIntensity) byz[h.Charge] = h;
            var list = byz.Values.ToList();
            double total = list.Sum(h => h.EnvelopeIntensity);
            double mass = total > 0.0 ? list.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / total : list.Average(h => h.ObservedMass);
            EnvelopeHit best = list.OrderByDescending(h => h.EnvelopeIntensity).First();
            var charges = list.Select(h => h.Charge).Distinct().OrderBy(x => x).ToList();
            int minz = charges.First(), maxz = charges.Last();
            int expectedApex = ExpectedApexIsotopeIndex(mass);
            double wAbsPpm = total > 0.0 ? list.Sum(h => Math.Abs(h.PpmError) * h.EnvelopeIntensity) / total : Median(list.Select(h => Math.Abs(h.PpmError)).ToList());
            return new ScanSummary { Scan = best.Scan, Rt = best.Rt, Mass = mass, MedianPpmError = Median(list.Select(h => h.PpmError).ToList()), WeightedAbsPpmError = wAbsPpm, ScanEnvelopeIntensity = total, MaxChargeEnvelopeIntensity = best.EnvelopeIntensity, BestCharge = best.Charge, BestChargeCosine = best.IsotopeCosineScore, MedianIsotopeCosine = Median(list.Select(h => h.IsotopeCosineScore).ToList()), MedianMatchedIsotopes = (int)Math.Round(Median(list.Select(h => (double)h.MatchedIsotopeCount).ToList())), BestApexIsotopeIndex = best.ApexIsotopeIndex, ExpectedApexIsotopeIndex = expectedApex, ApexIsotopeDelta = Math.Abs(best.ApexIsotopeIndex - expectedApex), MinCharge = minz, MaxCharge = maxz, ChargeCount = charges.Count, ChargeContinuity = charges.Count / (double)(maxz - minz + 1), EvidenceCount = hits.Count };
        }
        private static double WeightedMeanMass(List<EnvelopeHit> h) { double t = h.Sum(x => x.EnvelopeIntensity); return t > 0.0 ? h.Sum(x => x.ObservedMass * x.EnvelopeIntensity) / t : h.Average(x => x.ObservedMass); }

        private static List<FeatureGroup> BuildGlobalFeatures(List<ScanSummary> scans, double ppm, int minScans, int maxGap, double minTrace, double minRate, int minChargeCount, double minScore)
        {
            var outp = new List<FeatureGroup>(); if (scans.Count == 0) return outp;
            var curMass = new List<ScanSummary>(); double cen = double.NaN;
            foreach (ScanSummary s in scans.OrderBy(s => s.Mass))
            {
                if (curMass.Count == 0 || Math.Abs(PpmError(s.Mass, cen)) <= ppm) { curMass.Add(s); cen = WeightedMeanMassFromScans(curMass); }
                else { SplitByScan(curMass, maxGap, outp, minScans, minTrace, minRate, minChargeCount, minScore); curMass = new List<ScanSummary> { s }; cen = s.Mass; }
            }
            if (curMass.Count > 0) SplitByScan(curMass, maxGap, outp, minScans, minTrace, minRate, minChargeCount, minScore);
            return outp;
        }
        private static void SplitByScan(List<ScanSummary> cluster, int maxGap, List<FeatureGroup> outp, int minScans, double minTrace, double minRate, int minChargeCount, double minScore)
        {
            FeatureGroup f = null; ScanSummary prev = null;
            foreach (ScanSummary s in cluster.OrderBy(x => x.Scan))
            {
                if (f == null || (prev != null && s.Scan - prev.Scan > maxGap)) { if (f != null) outp.Add(FinalizeFeature(f, minScans, minTrace, minRate, minChargeCount, minScore)); f = new FeatureGroup(); }
                f.Scans.Add(s); prev = s;
            }
            if (f != null) outp.Add(FinalizeFeature(f, minScans, minTrace, minRate, minChargeCount, minScore));
        }
        private static FeatureGroup FinalizeFeature(FeatureGroup f, int minScans, double minTrace, double minRate, int minChargeCount, double minScore)
        {
            var s = f.Scans.OrderBy(x => x.Scan).ToList(); double total = s.Sum(x => x.ScanEnvelopeIntensity);
            ScanSummary apexSummary = s.OrderByDescending(x => x.ScanEnvelopeIntensity).First();
            f.SumIntensity = total; f.MaxScanIntensity = s.Max(x => x.ScanEnvelopeIntensity); f.Mass = total > 0.0 ? s.Sum(x => x.Mass * x.ScanEnvelopeIntensity) / total : s.Average(x => x.Mass);
            f.StartRt = s.First().Rt; f.EndRt = s.Last().Rt; f.ApexRt = apexSummary.Rt; f.TraceLengthSeconds = Math.Max(0.0, (f.EndRt - f.StartRt) * 60.0);
            f.MinCharge = s.Min(x => x.MinCharge); f.MaxCharge = s.Max(x => x.MaxCharge); f.ChargeCount = CountDistinctCharges(s); f.ChargeContinuity = f.MaxCharge >= f.MinCharge ? f.ChargeCount / (double)(f.MaxCharge - f.MinCharge + 1) : 1.0;
            f.MatchedScans = s.Select(x => x.Scan).Distinct().Count(); f.ScanSpan = s.Last().Scan - s.First().Scan + 1; f.SampleRate = f.ScanSpan > 0 ? f.MatchedScans / (double)f.ScanSpan : 1.0;
            f.MedianPpmError = Median(s.Select(x => x.MedianPpmError).ToList()); f.WeightedAbsPpmError = total > 0.0 ? s.Sum(x => x.WeightedAbsPpmError * x.ScanEnvelopeIntensity) / total : Median(s.Select(x => x.WeightedAbsPpmError).ToList()); f.PpmMad = MedianAbsoluteDeviation(s.Select(x => x.MedianPpmError).ToList()); f.MedianIsotopeCosine = Median(s.Select(x => x.MedianIsotopeCosine).ToList()); f.BestIsotopeCosine = s.Max(x => x.BestChargeCosine); f.MedianMatchedIsotopes = (int)Math.Round(Median(s.Select(x => (double)x.MedianMatchedIsotopes).ToList()));
            f.ObservedApexIsotopeIndex = apexSummary.BestApexIsotopeIndex; f.ExpectedApexIsotopeIndex = ExpectedApexIsotopeIndex(f.Mass); f.ApexIsotopeDelta = Math.Abs(f.ObservedApexIsotopeIndex - f.ExpectedApexIsotopeIndex);
            f.ApexDominance = f.SumIntensity > 0.0 ? f.MaxScanIntensity / f.SumIntensity : 0.0; f.FeatureScore = CalculateFeatureScore(f);
            f.MergedFeatureCount = 1; f.MergedIsotopeOffsets = "0"; f.RawAnchors.Add(MakeAnchor(f)); f.PreferredMass = f.Mass; f.RawMergedMassMin = f.Mass; f.RawMergedMassMax = f.Mass; f.RawMergedMassSpanDa = 0; f.MassInterpretation = "single accepted feature";
            var reasons = new List<string>(); if (f.MatchedScans < minScans) reasons.Add("scan_count_below_minimum"); if (f.TraceLengthSeconds < minTrace) reasons.Add("trace_length_below_minimum"); if (f.SampleRate < minRate) reasons.Add("sample_rate_below_minimum"); if (f.ChargeCount < minChargeCount) reasons.Add("charge_count_below_minimum"); if (f.FeatureScore < minScore) reasons.Add("feature_score_below_minimum"); f.Accepted = reasons.Count == 0; f.RejectReason = string.Join(";", reasons.ToArray()); return f;
        }

        private static List<FeatureGroup> CollapseIsotopeShiftedFeatures(List<FeatureGroup> features, double ppm, int maxShift, double apexTol, double rtOverlap)
        {
            var sorted = features.OrderByDescending(f => f.SumIntensity).ToList(); bool[] used = new bool[sorted.Count]; var outp = new List<FeatureGroup>();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (used[i]) continue; var c = new List<FeatureGroup> { sorted[i] }; var off = new List<int> { 0 }; used[i] = true;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int j = 0; j < sorted.Count; j++)
                    {
                        if (used[j]) continue;
                        foreach (FeatureGroup seed in c.ToList())
                        {
                            int n;
                            if (AreIsotopeShiftedDuplicates(seed, sorted[j], ppm, maxShift, apexTol, rtOverlap, out n)) { c.Add(sorted[j]); off.Add(n); used[j] = true; grew = true; break; }
                        }
                    }
                }
                outp.Add(MergeFeatureCluster(sorted[i], c, off, false));
            }
            return outp;
        }
        private static bool AreIsotopeShiftedDuplicates(FeatureGroup a, FeatureGroup b, double ppm, int maxShift, double apexTol, double rtOverlap, out int off)
        {
            off = 0; if (maxShift <= 0) return false; double d = b.Mass - a.Mass; int n = (int)Math.Round(d / C13MinusC12); if (n == 0 || Math.Abs(n) > maxShift) return false;
            if (Math.Abs(d - n * C13MinusC12) > Math.Max(0.05, a.Mass * ppm / 1e6)) return false; if (!ChargeRangesOverlap(a, b)) return false; if (RetentionTimeOverlapFraction(a, b) < rtOverlap && Math.Abs(a.ApexRt - b.ApexRt) > apexTol) return false; off = n; return true;
        }
        private static List<FeatureGroup> CollapseWeakSameMassFragments(List<FeatureGroup> features, double ppm, double maxGap, double weak)
        {
            var sorted = features.OrderByDescending(f => f.SumIntensity).ToList(); bool[] used = new bool[sorted.Count]; var outp = new List<FeatureGroup>();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (used[i]) continue; var c = new List<FeatureGroup> { sorted[i] }; var off = new List<int> { 0 }; used[i] = true;
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (used[j]) continue; double ratio = sorted[i].SumIntensity > 0.0 ? sorted[j].SumIntensity / sorted[i].SumIntensity : 1.0;
                    if (Math.Abs(PpmError(sorted[j].Mass, sorted[i].Mass)) <= ppm && ChargeRangesOverlap(sorted[i], sorted[j]) && ratio <= weak && RetentionTimeGap(sorted[i], sorted[j]) <= maxGap) { c.Add(sorted[j]); off.Add(0); used[j] = true; }
                }
                outp.Add(MergeFeatureCluster(sorted[i], c, off, true));
            }
            return outp;
        }

        private static FeatureGroup MergeFeatureCluster(FeatureGroup rep, List<FeatureGroup> cluster, List<int> offsets, bool sameMass)
        {
            if (cluster.Count == 1)
            {
                rep.RawMergedMassMin = rep.RawAnchors.Min(a => a.Mass); rep.RawMergedMassMax = rep.RawAnchors.Max(a => a.Mass); rep.RawMergedMassSpanDa = rep.RawMergedMassMax - rep.RawMergedMassMin; AnchorEvidence pa = SelectPreferredAnchor(rep.RawAnchors); rep.PreferredMass = pa != null ? pa.Mass : rep.Mass;
                rep.IsotopeAnchorAmbiguous = rep.MergedFeatureCount > 1 || rep.RawMergedMassSpanDa > 0.2; if (rep.IsotopeAnchorAmbiguous) rep.MassInterpretation = "isotope-anchor ambiguous collapsed feature; PreferredMonoisotopicMass selected by apex-isotope and ppm-centering"; return rep;
            }
            FeatureGroup m = new FeatureGroup(); m.Accepted = true; m.RejectReason = ""; m.RepresentativeFeatureIndex = rep.RepresentativeFeatureIndex > 0 ? rep.RepresentativeFeatureIndex : rep.FeatureIndex; double wsum = 0.0, msum = 0.0; var offstr = new List<string>();
            for (int i = 0; i < cluster.Count; i++)
            {
                FeatureGroup f = cluster[i]; int off = offsets[Math.Min(i, offsets.Count - 1)]; double adj = sameMass ? f.Mass : f.Mass - off * C13MinusC12; double w = Math.Max(1.0, f.SumIntensity); msum += adj * w; wsum += w; m.MergedFeatureCount += Math.Max(1, f.MergedFeatureCount); if (f.RawAnchors != null) m.RawAnchors.AddRange(f.RawAnchors); offstr.Add(!string.IsNullOrEmpty(f.MergedIsotopeOffsets) && f.MergedIsotopeOffsets != "0" ? f.MergedIsotopeOffsets : off.ToString(CultureInfo.InvariantCulture));
            }
            m.Mass = wsum > 0.0 ? msum / wsum : rep.Mass; m.SumIntensity = cluster.Max(f => f.SumIntensity); m.MaxScanIntensity = cluster.Max(f => f.MaxScanIntensity); m.StartRt = cluster.Min(f => f.StartRt); m.EndRt = cluster.Max(f => f.EndRt); m.ApexRt = cluster.OrderByDescending(f => f.MaxScanIntensity).First().ApexRt; m.TraceLengthSeconds = Math.Max(0.0, (m.EndRt - m.StartRt) * 60.0); m.Scans = cluster.SelectMany(f => f.Scans).ToList(); m.MinCharge = cluster.Min(f => f.MinCharge); m.MaxCharge = cluster.Max(f => f.MaxCharge); m.ChargeCount = CountDistinctChargesFromFeatures(cluster); m.ChargeContinuity = m.MaxCharge >= m.MinCharge ? m.ChargeCount / (double)(m.MaxCharge - m.MinCharge + 1) : 1.0;
            var scans = m.Scans.Select(s => s.Scan).Distinct().OrderBy(x => x).ToList(); m.MatchedScans = scans.Count; m.ScanSpan = scans.Count > 0 ? scans.Last() - scans.First() + 1 : cluster.Max(f => f.ScanSpan); m.SampleRate = m.ScanSpan > 0 ? m.MatchedScans / (double)m.ScanSpan : 1.0;
            FeatureGroup best = cluster.OrderByDescending(f => f.FeatureScore).First(); m.MedianPpmError = best.MedianPpmError; m.WeightedAbsPpmError = WeightedAverageFeatureValue(cluster, f => f.WeightedAbsPpmError); m.PpmMad = cluster.Max(f => f.PpmMad); m.MedianIsotopeCosine = WeightedAverageFeatureValue(cluster, f => f.MedianIsotopeCosine); m.BestIsotopeCosine = cluster.Max(f => f.BestIsotopeCosine); m.MedianMatchedIsotopes = (int)Math.Round(WeightedAverageFeatureValue(cluster, f => f.MedianMatchedIsotopes)); m.ApexDominance = m.SumIntensity > 0.0 ? m.MaxScanIntensity / m.SumIntensity : 0.0; m.FeatureScore = cluster.Max(f => f.FeatureScore); m.MergedIsotopeOffsets = string.Join(";", offstr.ToArray());
            m.RawMergedMassMin = m.RawAnchors.Min(a => a.Mass); m.RawMergedMassMax = m.RawAnchors.Max(a => a.Mass); m.RawMergedMassSpanDa = m.RawMergedMassMax - m.RawMergedMassMin; AnchorEvidence pref = SelectPreferredAnchor(m.RawAnchors); m.PreferredMass = pref != null ? pref.Mass : m.Mass; if (pref != null) { m.ObservedApexIsotopeIndex = pref.ObservedApexIsotopeIndex; m.ExpectedApexIsotopeIndex = pref.ExpectedApexIsotopeIndex; m.ApexIsotopeDelta = pref.ApexIsotopeDelta; }
            m.IsotopeAnchorAmbiguous = m.RawAnchors.Select(a => Math.Round(a.Mass, 3)).Distinct().Count() > 1 || Math.Abs(m.PreferredMass - m.Mass) > 0.2; m.MassInterpretation = m.IsotopeAnchorAmbiguous ? "isotope-anchor ambiguous collapsed feature; PreferredMonoisotopicMass selected by apex-isotope and ppm-centering" : (sameMass ? "same-mass weak RT fragments collapsed into dominant feature" : "isotope-offset collapsed feature"); return m;
        }
        private static AnchorEvidence MakeAnchor(FeatureGroup f) { return new AnchorEvidence { Mass = f.Mass, SumIntensity = f.SumIntensity, TraceLengthSeconds = f.TraceLengthSeconds, MatchedScans = f.MatchedScans, SampleRate = f.SampleRate, ChargeCount = f.ChargeCount, MedianIsotopeCosine = f.MedianIsotopeCosine, BestIsotopeCosine = f.BestIsotopeCosine, FeatureScore = f.FeatureScore, StartRt = f.StartRt, EndRt = f.EndRt, ApexRt = f.ApexRt, FeatureIndex = f.FeatureIndex, MedianPpmError = f.MedianPpmError, WeightedAbsPpmError = f.WeightedAbsPpmError, ObservedApexIsotopeIndex = f.ObservedApexIsotopeIndex, ExpectedApexIsotopeIndex = f.ExpectedApexIsotopeIndex, ApexIsotopeDelta = f.ApexIsotopeDelta }; }

        // Preferred anchor rule:
        //   first keep only well-supported anchors, then select the anchor whose observed isotope-envelope apex is
        //   closest to the expected observed isotope apex, approximated as round(mass/1800), then tie-break by ppm centering and support.
        // This fixes both observed cases:
        //   IgG: rejects +1/+4 isotope shifted labels and returns ~22572.99
        //   3_L: rejects the -1 isotope shifted label and ppm-offset label and returns ~22574.01

private static AnchorEvidence SelectPreferredAnchor(List<AnchorEvidence> anchors)
    {
    var clean = anchors.Where(a => !double.IsNaN(a.Mass) && a.Mass > 0.0).ToList();
    if (clean.Count == 0) return null;

    double maxI = clean.Max(a => a.SumIntensity);
    double maxT = clean.Max(a => a.TraceLengthSeconds);
    int maxS = clean.Max(a => a.MatchedScans);
    int maxC = clean.Max(a => a.ChargeCount);

    // FINAL SIMPLIFIED ANCHOR RULE:
    // The true monoisotopic anchor can be much weaker than +1/+2/+3 isotope-shifted anchors.
    // Do not require high relative intensity here.
    // Instead require real chromatographic/charge support and good ppm/apex quality,
    // then choose the lowest supported anchor.
    //
    // This fixes:
    // IgG: chooses 22572.96/22572.99 instead of 22573.970103.
    // 3_L: chooses 22574.008/22574.01 instead of 22575.013935,
    // while rejecting the weak 22572.997 fragment.

    int minGoodChargeCount = Math.Min(maxC, Math.Max(3, maxC / 2));
    int minGoodScans = Math.Max(10, (int)Math.Round(maxS * 0.02));
    double minGoodTraceSeconds = Math.Max(20.0, maxT * 0.01);

    var hard = clean.Where(a =>
        a.ChargeCount >= minGoodChargeCount &&
        a.MatchedScans >= minGoodScans &&
        a.TraceLengthSeconds >= minGoodTraceSeconds &&
        a.ApexIsotopeDelta <= 1.0 &&
        (Math.Abs(a.MedianPpmError) <= 1.0 || a.WeightedAbsPpmError <= 1.0)
    ).ToList();

    if (hard.Count > 0)
    {
        return hard
            .OrderBy(a => a.Mass)
            .ThenBy(a => a.ApexIsotopeDelta)
            .ThenBy(a => a.WeightedAbsPpmError)
            .ThenBy(a => Math.Abs(a.MedianPpmError))
            .ThenByDescending(a => a.ChargeCount)
            .ThenByDescending(a => a.MatchedScans)
            .ThenByDescending(a => a.SumIntensity)
            .First();
    }

    // Fallback for sparse or low-quality features.
    var eligible = clean.Where(a =>
        a.SumIntensity >= maxI * 0.01 &&
        a.TraceLengthSeconds >= maxT * 0.05 &&
        a.MatchedScans >= Math.Max(3, (int)Math.Round(maxS * 0.05)) &&
        a.ChargeCount >= Math.Max(3, maxC - 8)
    ).ToList();

    if (eligible.Count == 0) eligible = clean;

    return eligible
        .OrderBy(a => a.ApexIsotopeDelta)
        .ThenBy(a => a.WeightedAbsPpmError)
        .ThenBy(a => Math.Abs(a.MedianPpmError))
        .ThenByDescending(a => a.ChargeCount)
        .ThenByDescending(a => a.MatchedScans)
        .ThenByDescending(a => a.SumIntensity)
        .ThenBy(a => a.Mass)
        .First();
}
        private static int CountDistinctChargesFromFeatures(List<FeatureGroup> features) { var set = new HashSet<int>(); foreach (FeatureGroup f in features) for (int z = f.MinCharge; z <= f.MaxCharge; z++) set.Add(z); return set.Count; }
        private static double RetentionTimeOverlapFraction(FeatureGroup a, FeatureGroup b) { double l = Math.Max(a.StartRt, b.StartRt), r = Math.Min(a.EndRt, b.EndRt); if (r < l) return 0.0; return (r - l) / Math.Max(0.000001, Math.Min(a.EndRt - a.StartRt, b.EndRt - b.StartRt)); }
        private static double RetentionTimeGap(FeatureGroup a, FeatureGroup b) { if (b.EndRt < a.StartRt) return a.StartRt - b.EndRt; if (a.EndRt < b.StartRt) return b.StartRt - a.EndRt; return 0.0; }
        private static bool ChargeRangesOverlap(FeatureGroup a, FeatureGroup b) { return Math.Max(a.MinCharge, b.MinCharge) <= Math.Min(a.MaxCharge, b.MaxCharge); }
        private static int CountDistinctCharges(List<ScanSummary> scans) { var set = new HashSet<int>(); foreach (ScanSummary s in scans) for (int z = s.MinCharge; z <= s.MaxCharge; z++) set.Add(z); return set.Count; }
        private static double CalculateFeatureScore(FeatureGroup f) { return 0.35 * Clamp01((f.MedianIsotopeCosine - 0.70) / 0.30) + 0.20 * Clamp01(f.ChargeContinuity) * Clamp01(f.ChargeCount / 6.0) + 0.15 * Clamp01(f.MatchedScans / 8.0) + 0.15 * Clamp01(f.SampleRate) + 0.10 * Clamp01(1.0 - Math.Abs(f.MedianPpmError) / 20.0) + 0.05 * Clamp01(f.ApexDominance * 5.0); }
        private static double Clamp01(double x) { if (double.IsNaN(x) || x < 0.0) return 0.0; if (x > 1.0) return 1.0; return x; }
        private static double WeightedMeanMassFromScans(List<ScanSummary> scans) { double t = scans.Sum(s => s.ScanEnvelopeIntensity); return t > 0.0 ? scans.Sum(s => s.Mass * s.ScanEnvelopeIntensity) / t : scans.Average(s => s.Mass); }
        private static double WeightedAverageFeatureValue(List<FeatureGroup> c, Func<FeatureGroup, double> sel) { double w = 0.0, v = 0.0; foreach (FeatureGroup f in c) { double x = sel(f); if (double.IsNaN(x)) continue; double ww = Math.Max(1.0, f.SumIntensity); v += x * ww; w += ww; } return w > 0.0 ? v / w : double.NaN; }

        private static void WriteEvidence(string path, List<EnvelopeHit> hits) { using (var w = new StreamWriter(path)) { w.WriteLine("Scan\tRT\tCandidateMass\tObservedMass\tPpmError\tCharge\tSeedIsotopeIndex\tBaseMz\tEnvelopeIntensity\tMaxIsotopeIntensity\tApexIsotopeIndex\tMatchedIsotopeCount\tTotalIsotopeCount\tIsotopeCosineScore\tBasePeakMass\tTIC"); foreach (EnvelopeHit h in hits.OrderBy(h => h.ObservedMass).ThenBy(h => h.Scan).ThenBy(h => h.Charge)) w.WriteLine(Join(h.Scan, F(h.Rt), F(h.CandidateMass), F(h.ObservedMass), h.PpmError.ToString("F4", CultureInfo.InvariantCulture), h.Charge, h.SeedIsotopeIndex, F(h.BaseMz), G(h.EnvelopeIntensity), G(h.MaxIsotopeIntensity), h.ApexIsotopeIndex, h.MatchedIsotopeCount, h.TotalIsotopeCount, h.IsotopeCosineScore.ToString("F6", CultureInfo.InvariantCulture), G(h.BasePeakMass), G(h.Tic))); } }
        private static void WriteScanSummary(string path, List<ScanSummary> s) { using (var w = new StreamWriter(path)) { w.WriteLine("Scan\tRT\tMass\tMedianPpmError\tWeightedAbsPpmError\tScanEnvelopeIntensity\tMaxChargeEnvelopeIntensity\tBestCharge\tBestChargeCosine\tMedianIsotopeCosine\tMedianMatchedIsotopes\tBestApexIsotopeIndex\tExpectedApexIsotopeIndex\tApexIsotopeDelta\tMinCharge\tMaxCharge\tChargeCount\tChargeContinuity\tEvidenceCount"); foreach (ScanSummary x in s.OrderBy(x => x.Mass).ThenBy(x => x.Scan)) w.WriteLine(Join(x.Scan, F(x.Rt), F(x.Mass), x.MedianPpmError.ToString("F4", CultureInfo.InvariantCulture), x.WeightedAbsPpmError.ToString("F4", CultureInfo.InvariantCulture), G(x.ScanEnvelopeIntensity), G(x.MaxChargeEnvelopeIntensity), x.BestCharge, x.BestChargeCosine.ToString("F6", CultureInfo.InvariantCulture), x.MedianIsotopeCosine.ToString("F6", CultureInfo.InvariantCulture), x.MedianMatchedIsotopes, x.BestApexIsotopeIndex, x.ExpectedApexIsotopeIndex, x.ApexIsotopeDelta.ToString("F3", CultureInfo.InvariantCulture), x.MinCharge, x.MaxCharge, x.ChargeCount, x.ChargeContinuity.ToString("F6", CultureInfo.InvariantCulture), x.EvidenceCount)); } }
        private static void WriteFeatureSummary(string path, List<FeatureGroup> features, bool rejected)
        {
            using (var w = new StreamWriter(path))
            {
                string h = "FeatureIndex\tReportedMonoisotopicMass\tPreferredMonoisotopicMass\tRawMergedMassMin\tRawMergedMassMax\tRawMergedMassSpanDa\tIsotopeAnchorAmbiguous\tMassInterpretation\tStartRetentionTime\tEndRetentionTime\tApexRetentionTime\tTraceLengthSeconds\tSumIntensity\tMaxScanIntensity\tMinCharge\tMaxCharge\tChargeCount\tChargeContinuity\tMatchedScans\tScanSpan\tSampleRate\tMedianPpmError\tWeightedAbsPpmError\tPpmMAD\tMedianIsotopeCosineScore\tBestIsotopeCosineScore\tMedianMatchedIsotopes\tObservedApexIsotopeIndex\tExpectedApexIsotopeIndex\tApexIsotopeDelta\tApexDominance\tFeatureScore\tMergedFeatureCount\tMergedIsotopeOffsets\tRepresentativeFeatureIndex";
                if (rejected) h += "\tRejectReason"; w.WriteLine(h);
                foreach (FeatureGroup f in features)
                {
                    if (f.PreferredMass == 0.0) f.PreferredMass = f.RawMergedMassMin > 0.0 ? f.RawMergedMassMin : f.Mass;
                    string line = Join(f.FeatureIndex, F(f.Mass), F(f.PreferredMass), F(f.RawMergedMassMin), F(f.RawMergedMassMax), F(f.RawMergedMassSpanDa), f.IsotopeAnchorAmbiguous ? "1" : "0", Safe(f.MassInterpretation), F(f.StartRt), F(f.EndRt), F(f.ApexRt), f.TraceLengthSeconds.ToString("F3", CultureInfo.InvariantCulture), G(f.SumIntensity), G(f.MaxScanIntensity), f.MinCharge, f.MaxCharge, f.ChargeCount, f.ChargeContinuity.ToString("F6", CultureInfo.InvariantCulture), f.MatchedScans, f.ScanSpan, f.SampleRate.ToString("F6", CultureInfo.InvariantCulture), f.MedianPpmError.ToString("F4", CultureInfo.InvariantCulture), f.WeightedAbsPpmError.ToString("F4", CultureInfo.InvariantCulture), f.PpmMad.ToString("F4", CultureInfo.InvariantCulture), f.MedianIsotopeCosine.ToString("F6", CultureInfo.InvariantCulture), f.BestIsotopeCosine.ToString("F6", CultureInfo.InvariantCulture), f.MedianMatchedIsotopes, f.ObservedApexIsotopeIndex, f.ExpectedApexIsotopeIndex, f.ApexIsotopeDelta.ToString("F3", CultureInfo.InvariantCulture), f.ApexDominance.ToString("F6", CultureInfo.InvariantCulture), f.FeatureScore.ToString("F6", CultureInfo.InvariantCulture), f.MergedFeatureCount, Safe(f.MergedIsotopeOffsets), f.RepresentativeFeatureIndex);
                    if (rejected) line += "\t" + Safe(f.RejectReason); w.WriteLine(line);
                }
            }
        }
        private static string Join(params object[] items) { return string.Join("\t", items.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray()); }
        private static string F(double x) { return x.ToString("F6", CultureInfo.InvariantCulture); }
        private static string G(double x) { return x.ToString("G17", CultureInfo.InvariantCulture); }
        private static string Safe(string s) { return s == null ? "" : s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }
        private static double Cosine(double[] a, double[] b) { double dot = 0.0, aa = 0.0, bb = 0.0; int n = Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; } return aa <= 0.0 || bb <= 0.0 ? 0.0 : dot / Math.Sqrt(aa * bb); }
        private static double PpmError(double obs, double exp) { return exp == 0.0 || double.IsNaN(obs) || double.IsNaN(exp) ? double.NaN : (obs - exp) / exp * 1e6; }
        private static double Median(List<double> v) { var c = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList(); if (c.Count == 0) return double.NaN; int m = c.Count / 2; return c.Count % 2 == 1 ? c[m] : 0.5 * (c[m - 1] + c[m]); }
        private static double MedianAbsoluteDeviation(List<double> v) { var c = v.Where(x => !double.IsNaN(x)).ToList(); if (c.Count == 0) return double.NaN; double med = Median(c); return Median(c.Select(x => Math.Abs(x - med)).ToList()); }
        private static void Normalize(double[] v) { double s = v.Sum(); if (s <= 0.0) return; for (int i = 0; i < v.Length; i++) v[i] /= s; }
        private static void ReportProgress() { int done = Interlocked.Increment(ref ProgressDone); int p = (int)(done * 100L / Math.Max(1, ProgressTotal)); if (p != ProgressLastPercent) lock (ProgressLock) if (p != ProgressLastPercent) { ProgressLastPercent = p; PrintProgress(p); } }
        private static void PrintProgress(int p) { int w = 10, f = p * w / 100; Console.Write("\rProgress: [" + new string('#', f) + new string('-', w - f) + "] " + p.ToString().PadLeft(3) + "%"); }
    }
}
