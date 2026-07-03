// targeted isotope-envelope deconvolution-lite for Thermo RAW
// released under GPL version 2 or later: sharma.animesh@gmail.com
// compile: mcs deconvRaw.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:deconvRaw.exe
// run: mono deconvRaw.exe 260629_Solveig_3_L.raw 10000 100000 5 80 10 1000000 10000000 0.85 5 3 2 45
// args: raw file minMass maxMass minCharge maxCharge ppmTolerance minSeedIntensity minEnvelopeIntensity minCos minMatchedIsotopes minFeatureScans maxGapScans maxSeedIsotopeIndex
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.Data.Business;

namespace DeconvRawDiscovery
{
    internal class DeconvRaw
    {
        private const double Proton = 1.007276466812;
        private const double C13MinusC12 = 1.00335483507;

        private class PeakHit
        {
            public int Index;
            public double Mz;
            public double Intensity;
        }

        private class EnvelopeHit
        {
            public int Scan;
            public double Rt;
            public double CandidateMass;
            public double ObservedMass;
            public double PpmError;
            public int Charge;
            public int SeedIsotopeIndex;
            public double BaseMz;
            public double EnvelopeIntensity;
            public double MaxIsotopeIntensity;
            public int ApexIsotopeIndex;
            public int MatchedIsotopeCount;
            public int TotalIsotopeCount;
            public double IsotopeCosineScore;
            public double BasePeakMass;
            public double Tic;
        }

        private class ScanSummary
        {
            public int Scan;
            public double Rt;
            public double Mass;
            public double MedianPpmError;
            public double ScanEnvelopeIntensity;
            public double MaxChargeEnvelopeIntensity;
            public int BestCharge;
            public double BestChargeCosine;
            public int MinCharge;
            public int MaxCharge;
            public int ChargeCount;
            public int EvidenceCount;
        }

        private class FeatureGroup
        {
            public int FeatureIndex;
            public double Mass;
            public List<ScanSummary> Scans = new List<ScanSummary>();
            public bool Accepted;
            public string RejectReason;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("USAGE: {0} file.raw [minMass=10000] [maxMass=100000] [minCharge=1] [maxCharge=80] [ppmTolerance=10] [minSeedIntensity=1000000] [minEnvelopeIntensity=10000000] [minCos=0.85] [minMatchedIsotopes=5] [minFeatureScans=3] [maxGapScans=2] [maxSeedIsotopeIndex=45]", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];

            double minMass = 10000.0;
            double maxMass = 100000.0;
            int minCharge = 1;
            int maxCharge = 80;
            double ppmTolerance = 10.0;
            double minSeedIntensity = 1000000.0;
            double minEnvelopeIntensity = 10000000.0;
            double minCos = 0.85;
            int minMatchedIsotopes = 5;
            int minFeatureScans = 3;
            int maxGapScans = 2;
            int maxSeedIsotopeIndex = 45;

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

            if (minCharge <= 0) minCharge = 1;
            if (maxCharge < minCharge) maxCharge = minCharge;
            if (maxSeedIsotopeIndex < 0) maxSeedIsotopeIndex = 0;

            var rawFile = RawFileReaderAdapter.FileFactory(rawPath);

            if (!rawFile.IsOpen || rawFile.IsError)
            {
                Console.Error.WriteLine("Error opening raw file: {0} FileError: {1}", rawPath, rawFile.FileError);
                return;
            }

            rawFile.SelectInstrument(Device.MS, 1);

            int firstScan = rawFile.RunHeaderEx.FirstSpectrum;
            int lastScan = rawFile.RunHeaderEx.LastSpectrum;
            int scanCount = lastScan - firstScan + 1;

            Console.WriteLine("#filename:\t{0}", rawFile.FileName);
            Console.WriteLine("#scans:\t{0}", scanCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minMass:\t{0}", minMass.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxMass:\t{0}", maxMass.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minCharge:\t{0}", minCharge.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxCharge:\t{0}", maxCharge.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#ppmTolerance:\t{0}", ppmTolerance.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minSeedIntensity:\t{0}", minSeedIntensity.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minEnvelopeIntensity:\t{0}", minEnvelopeIntensity.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minCos:\t{0}", minCos.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minMatchedIsotopes:\t{0}", minMatchedIsotopes.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minFeatureScans:\t{0}", minFeatureScans.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxGapScans:\t{0}", maxGapScans.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxSeedIsotopeIndex:\t{0}", maxSeedIsotopeIndex.ToString(CultureInfo.InvariantCulture));

            List<EnvelopeHit> allHits = new List<EnvelopeHit>();
            List<ScanSummary> allScanSummaries = new List<ScanSummary>();

            int lastPercent = -1;

            for (int scanNumber = firstScan; scanNumber <= lastScan; scanNumber++)
            {
                double rt = rawFile.RetentionTimeFromScanNumber(scanNumber);
                var scanStatistics = rawFile.GetScanStatsForScanNumber(scanNumber);

                string scanEventText = "";

                try
                {
                    scanEventText = string.Join(" ", rawFile.GetScanEventForScanNumber(scanNumber));
                }
                catch
                {
                    scanEventText = "";
                }

                bool looksLikeMs1 =
                    scanEventText.IndexOf(" ms ", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    scanEventText.IndexOf("@", StringComparison.OrdinalIgnoreCase) < 0;

                if (looksLikeMs1)
                {
                    CentroidStream centroidStream = null;

                    try
                    {
                        centroidStream = rawFile.GetCentroidStream(scanNumber, false);
                    }
                    catch
                    {
                        centroidStream = null;
                    }

                    if (centroidStream != null && centroidStream.Length > 0)
                    {
                        List<EnvelopeHit> scanHits = DiscoverScanMasses(
                            scanNumber,
                            rt,
                            scanStatistics,
                            centroidStream,
                            minMass,
                            maxMass,
                            minCharge,
                            maxCharge,
                            ppmTolerance,
                            minSeedIntensity,
                            minEnvelopeIntensity,
                            minCos,
                            minMatchedIsotopes,
                            maxSeedIsotopeIndex
                        );

                        List<ScanSummary> scanSummaries = CollapseScanHits(scanHits, ppmTolerance);

                        allHits.AddRange(scanHits);
                        allScanSummaries.AddRange(scanSummaries);
                    }
                }

                int processed = scanNumber - firstScan + 1;
                int percent = (int)((processed * 100L) / scanCount);

                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    PrintProgress(percent);
                }
            }

            Console.WriteLine();
            rawFile.Dispose();

            List<FeatureGroup> features = BuildGlobalFeatures(allScanSummaries, ppmTolerance, minFeatureScans, maxGapScans);

            int idx = 1;
            foreach (FeatureGroup f in features.Where(f => f.Accepted).OrderByDescending(f => f.Scans.Sum(s => s.ScanEnvelopeIntensity)))
            {
                f.FeatureIndex = idx;
                idx++;
            }

            idx = 1;
            foreach (FeatureGroup f in features.Where(f => !f.Accepted).OrderByDescending(f => f.Scans.Sum(s => s.ScanEnvelopeIntensity)))
            {
                f.FeatureIndex = idx;
                idx++;
            }

            string prefix = rawPath + ".discovery";

            string evidenceFile = prefix + ".scan_evidence.tsv";
            string scanSummaryFile = prefix + ".scan_summary.tsv";
            string featureFile = prefix + ".deconv_masses.tsv";
            string rejectedFile = prefix + ".rejected_masses.tsv";

            WriteEvidence(evidenceFile, allHits);
            WriteScanSummary(scanSummaryFile, allScanSummaries);
            WriteFeatureSummary(featureFile, features.Where(f => f.Accepted).OrderByDescending(f => f.Scans.Sum(s => s.ScanEnvelopeIntensity)).ToList());
            WriteRejectedFeatureSummary(rejectedFile, features.Where(f => !f.Accepted).OrderByDescending(f => f.Scans.Sum(s => s.ScanEnvelopeIntensity)).ToList());

            Console.WriteLine("Wrote scan evidence: {0}", evidenceFile);
            Console.WriteLine("Wrote scan summary: {0}", scanSummaryFile);
            Console.WriteLine("Wrote deconvolved masses: {0}", featureFile);
            Console.WriteLine("Wrote rejected masses: {0}", rejectedFile);
        }

        private static List<EnvelopeHit> DiscoverScanMasses(
            int scanNumber,
            double rt,
            dynamic scanStatistics,
            CentroidStream centroidStream,
            double minMass,
            double maxMass,
            int minCharge,
            int maxCharge,
            double ppmTolerance,
            double minSeedIntensity,
            double minEnvelopeIntensity,
            double minCos,
            int minMatchedIsotopes,
            int maxSeedIsotopeIndex)
        {
            List<EnvelopeHit> hits = new List<EnvelopeHit>();

            double[] mzArray = centroidStream.Masses;
            double[] intensityArray = centroidStream.Intensities;
            double[] chargeArray = null;

            try
            {
                chargeArray = centroidStream.Charges;
            }
            catch
            {
                chargeArray = null;
            }

            for (int peakIndex = 0; peakIndex < mzArray.Length; peakIndex++)
            {
                double seedMz = mzArray[peakIndex];
                double seedIntensity = intensityArray[peakIndex];

                if (seedIntensity < minSeedIntensity)
                    continue;

                List<int> charges = GetCandidateCharges(chargeArray, peakIndex, minCharge, maxCharge);

                for (int ci = 0; ci < charges.Count; ci++)
                {
                    int charge = charges[ci];

                    for (int seedIso = 0; seedIso <= maxSeedIsotopeIndex; seedIso++)
                    {
                        double candidateMass = seedMz * charge - charge * Proton - seedIso * C13MinusC12;

                        if (candidateMass < minMass || candidateMass > maxMass)
                            continue;

                        EnvelopeHit hit = TryMatchEnvelope(
                            scanNumber,
                            rt,
                            scanStatistics,
                            candidateMass,
                            charge,
                            seedIso,
                            mzArray,
                            intensityArray,
                            ppmTolerance,
                            minEnvelopeIntensity,
                            minCos,
                            minMatchedIsotopes
                        );

                        if (hit != null)
                            hits.Add(hit);
                    }
                }
            }

            return hits;
        }

        private static List<int> GetCandidateCharges(double[] chargeArray, int peakIndex, int minCharge, int maxCharge)
        {
            List<int> charges = new List<int>();

            int thermoCharge = 0;

            try
            {
                if (chargeArray != null && peakIndex < chargeArray.Length)
                    thermoCharge = (int)Math.Round(chargeArray[peakIndex]);
            }
            catch
            {
                thermoCharge = 0;
            }

            if (thermoCharge >= minCharge && thermoCharge <= maxCharge)
            {
                charges.Add(thermoCharge);
            }
            else
            {
                for (int z = minCharge; z <= maxCharge; z++)
                    charges.Add(z);
            }

            return charges;
        }

        private static EnvelopeHit TryMatchEnvelope(
            int scanNumber,
            double rt,
            dynamic scanStatistics,
            double candidateMass,
            int charge,
            int seedIsotopeIndex,
            double[] mzArray,
            double[] intensityArray,
            double ppmTolerance,
            double minEnvelopeIntensity,
            double minCos,
            int minMatchedIsotopes)
        {
            if (charge <= 0)
                return null;

            int isotopeCount = EstimateIsotopeCount(candidateMass);
            double[] theoretical = BuildPoissonEnvelope(candidateMass, isotopeCount);
            double[] observed = new double[isotopeCount];

            double envelopeIntensity = 0.0;
            double maxIsotopeIntensity = 0.0;
            int apexIsotopeIndex = -1;
            int matchedIsotopeCount = 0;

            double observedMassWeightedSum = 0.0;
            double observedMassWeight = 0.0;

            for (int isotopeIndex = 0; isotopeIndex < isotopeCount; isotopeIndex++)
            {
                double expectedMz = (candidateMass + isotopeIndex * C13MinusC12 + charge * Proton) / charge;
                double toleranceDa = expectedMz * ppmTolerance / 1e6;

                PeakHit peak = FindHighestPeak(mzArray, intensityArray, expectedMz - toleranceDa, expectedMz + toleranceDa);

                if (peak != null)
                {
                    observed[isotopeIndex] = peak.Intensity;
                    envelopeIntensity += peak.Intensity;
                    matchedIsotopeCount++;

                    if (peak.Intensity > maxIsotopeIntensity)
                    {
                        maxIsotopeIntensity = peak.Intensity;
                        apexIsotopeIndex = isotopeIndex;
                    }

                    double observedNeutralMassForThisIsotope = peak.Mz * charge - charge * Proton - isotopeIndex * C13MinusC12;
                    observedMassWeightedSum += observedNeutralMassForThisIsotope * peak.Intensity;
                    observedMassWeight += peak.Intensity;
                }
            }

            if (matchedIsotopeCount < minMatchedIsotopes)
                return null;

            if (envelopeIntensity < minEnvelopeIntensity)
                return null;

            double cosine = Cosine(observed, theoretical);

            if (cosine < minCos)
                return null;

            double observedMass = observedMassWeight > 0.0 ? observedMassWeightedSum / observedMassWeight : double.NaN;
            double ppmError = PpmError(observedMass, candidateMass);
            double baseMz = (candidateMass + charge * Proton) / charge;

            return new EnvelopeHit
            {
                Scan = scanNumber,
                Rt = rt,
                CandidateMass = candidateMass,
                ObservedMass = observedMass,
                PpmError = ppmError,
                Charge = charge,
                SeedIsotopeIndex = seedIsotopeIndex,
                BaseMz = baseMz,
                EnvelopeIntensity = envelopeIntensity,
                MaxIsotopeIntensity = maxIsotopeIntensity,
                ApexIsotopeIndex = apexIsotopeIndex,
                MatchedIsotopeCount = matchedIsotopeCount,
                TotalIsotopeCount = isotopeCount,
                IsotopeCosineScore = cosine,
                BasePeakMass = scanStatistics.BasePeakMass,
                Tic = scanStatistics.TIC
            };
        }

        private static List<ScanSummary> CollapseScanHits(List<EnvelopeHit> scanHits, double ppmTolerance)
        {
            List<ScanSummary> output = new List<ScanSummary>();

            if (scanHits.Count == 0)
                return output;

            List<EnvelopeHit> sorted = scanHits.OrderBy(h => h.ObservedMass).ToList();

            List<EnvelopeHit> current = new List<EnvelopeHit>();
            double currentCenter = double.NaN;

            for (int i = 0; i < sorted.Count; i++)
            {
                EnvelopeHit hit = sorted[i];

                if (current.Count == 0)
                {
                    current.Add(hit);
                    currentCenter = hit.ObservedMass;
                    continue;
                }

                double ppm = Math.Abs(PpmError(hit.ObservedMass, currentCenter));

                if (ppm <= ppmTolerance)
                {
                    current.Add(hit);
                    currentCenter = WeightedMeanMass(current);
                }
                else
                {
                    output.Add(MakeScanSummary(current));
                    current.Clear();
                    current.Add(hit);
                    currentCenter = hit.ObservedMass;
                }
            }

            if (current.Count > 0)
                output.Add(MakeScanSummary(current));

            return output;
        }

        private static ScanSummary MakeScanSummary(List<EnvelopeHit> hits)
        {
            Dictionary<int, EnvelopeHit> bestByCharge = new Dictionary<int, EnvelopeHit>();

            foreach (EnvelopeHit h in hits)
            {
                if (!bestByCharge.ContainsKey(h.Charge) || h.EnvelopeIntensity > bestByCharge[h.Charge].EnvelopeIntensity)
                    bestByCharge[h.Charge] = h;
            }

            List<EnvelopeHit> list = bestByCharge.Values.ToList();

            double totalIntensity = list.Sum(h => h.EnvelopeIntensity);
            double mass = totalIntensity > 0.0 ? list.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / totalIntensity : list.Average(h => h.ObservedMass);
            List<double> ppmErrors = list.Select(h => h.PpmError).ToList();

            EnvelopeHit best = list.OrderByDescending(h => h.EnvelopeIntensity).First();
            List<int> charges = list.Select(h => h.Charge).Distinct().OrderBy(z => z).ToList();

            return new ScanSummary
            {
                Scan = best.Scan,
                Rt = best.Rt,
                Mass = mass,
                MedianPpmError = Median(ppmErrors),
                ScanEnvelopeIntensity = totalIntensity,
                MaxChargeEnvelopeIntensity = best.EnvelopeIntensity,
                BestCharge = best.Charge,
                BestChargeCosine = best.IsotopeCosineScore,
                MinCharge = charges.First(),
                MaxCharge = charges.Last(),
                ChargeCount = charges.Count,
                EvidenceCount = hits.Count
            };
        }

        private static double WeightedMeanMass(List<EnvelopeHit> hits)
        {
            double total = hits.Sum(h => h.EnvelopeIntensity);

            if (total <= 0)
                return hits.Average(h => h.ObservedMass);

            return hits.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / total;
        }

        private static List<FeatureGroup> BuildGlobalFeatures(List<ScanSummary> scanSummaries, double ppmTolerance, int minFeatureScans, int maxGapScans)
        {
            List<FeatureGroup> output = new List<FeatureGroup>();

            if (scanSummaries.Count == 0)
                return output;

            List<ScanSummary> sortedByMass = scanSummaries.OrderBy(s => s.Mass).ToList();
            List<List<ScanSummary>> massClusters = new List<List<ScanSummary>>();

            List<ScanSummary> current = new List<ScanSummary>();
            double centerMass = double.NaN;

            foreach (ScanSummary s in sortedByMass)
            {
                if (current.Count == 0)
                {
                    current.Add(s);
                    centerMass = s.Mass;
                    continue;
                }

                double ppm = Math.Abs(PpmError(s.Mass, centerMass));

                if (ppm <= ppmTolerance)
                {
                    current.Add(s);
                    centerMass = WeightedMeanMassFromScans(current);
                }
                else
                {
                    massClusters.Add(current);
                    current = new List<ScanSummary>();
                    current.Add(s);
                    centerMass = s.Mass;
                }
            }

            if (current.Count > 0)
                massClusters.Add(current);

            foreach (List<ScanSummary> massCluster in massClusters)
            {
                List<ScanSummary> ordered = massCluster.OrderBy(s => s.Scan).ToList();

                FeatureGroup feature = null;
                ScanSummary previous = null;

                foreach (ScanSummary scan in ordered)
                {
                    bool startNew = false;

                    if (feature == null)
                        startNew = true;
                    else if (previous != null && scan.Scan - previous.Scan > maxGapScans)
                        startNew = true;

                    if (startNew)
                    {
                        if (feature != null)
                            output.Add(FinalizeFeature(feature, minFeatureScans));

                        feature = new FeatureGroup();
                    }

                    feature.Scans.Add(scan);
                    previous = scan;
                }

                if (feature != null)
                    output.Add(FinalizeFeature(feature, minFeatureScans));
            }

            return output;
        }

        private static FeatureGroup FinalizeFeature(FeatureGroup feature, int minFeatureScans)
        {
            double totalIntensity = feature.Scans.Sum(s => s.ScanEnvelopeIntensity);

            if (totalIntensity > 0.0)
                feature.Mass = feature.Scans.Sum(s => s.Mass * s.ScanEnvelopeIntensity) / totalIntensity;
            else
                feature.Mass = feature.Scans.Average(s => s.Mass);

            int scanCount = feature.Scans.Select(s => s.Scan).Distinct().Count();

            if (scanCount >= minFeatureScans)
            {
                feature.Accepted = true;
                feature.RejectReason = "";
            }
            else
            {
                feature.Accepted = false;
                feature.RejectReason = "scan_count_below_minimum";
            }

            return feature;
        }

        private static double WeightedMeanMassFromScans(List<ScanSummary> scans)
        {
            double total = scans.Sum(s => s.ScanEnvelopeIntensity);

            if (total <= 0)
                return scans.Average(s => s.Mass);

            return scans.Sum(s => s.Mass * s.ScanEnvelopeIntensity) / total;
        }

        private static int EstimateIsotopeCount(double neutralMass)
        {
            double lambda = EstimateCarbonLambda(neutralMass);
            int count = (int)Math.Ceiling(lambda + 8.0 * Math.Sqrt(lambda) + 10.0);

            if (count < 16)
                count = 16;

            if (count > 120)
                count = 120;

            return count;
        }

        private static double EstimateCarbonLambda(double neutralMass)
        {
            return neutralMass / 1800.0;
        }

        private static double[] BuildPoissonEnvelope(double neutralMass, int isotopeCount)
        {
            double lambda = EstimateCarbonLambda(neutralMass);
            double[] envelope = new double[isotopeCount];

            envelope[0] = Math.Exp(-lambda);

            for (int i = 1; i < isotopeCount; i++)
                envelope[i] = envelope[i - 1] * lambda / i;

            double sum = envelope.Sum();

            if (sum > 0.0)
            {
                for (int i = 0; i < envelope.Length; i++)
                    envelope[i] /= sum;
            }

            return envelope;
        }

        private static PeakHit FindHighestPeak(double[] mzArray, double[] intensityArray, double minMz, double maxMz)
        {
            int start = LowerBound(mzArray, minMz);
            PeakHit best = null;

            for (int i = start; i < mzArray.Length; i++)
            {
                if (mzArray[i] > maxMz)
                    break;

                if (mzArray[i] < minMz)
                    continue;

                if (best == null || intensityArray[i] > best.Intensity)
                {
                    best = new PeakHit
                    {
                        Index = i,
                        Mz = mzArray[i],
                        Intensity = intensityArray[i]
                    };
                }
            }

            return best;
        }

        private static int LowerBound(double[] array, double value)
        {
            int left = 0;
            int right = array.Length;

            while (left < right)
            {
                int mid = left + (right - left) / 2;

                if (array[mid] < value)
                    left = mid + 1;
                else
                    right = mid;
            }

            return left;
        }

        private static void WriteEvidence(string path, List<EnvelopeHit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tCandidateMass\tObservedMass\tPpmError\tCharge\tSeedIsotopeIndex\tBaseMz\tEnvelopeIntensity\tMaxIsotopeIntensity\tApexIsotopeIndex\tMatchedIsotopeCount\tTotalIsotopeCount\tIsotopeCosineScore\tBasePeakMass\tTIC");

                foreach (EnvelopeHit h in hits.OrderBy(h => h.ObservedMass).ThenBy(h => h.Scan).ThenBy(h => h.Charge))
                {
                    writer.WriteLine(
                        h.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.CandidateMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.ObservedMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.PpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                        h.Charge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.SeedIsotopeIndex.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.BaseMz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.EnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        h.MaxIsotopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        h.ApexIsotopeIndex.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.MatchedIsotopeCount.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.TotalIsotopeCount.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.IsotopeCosineScore.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.BasePeakMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        h.Tic.ToString("G17", CultureInfo.InvariantCulture)
                    );
                }
            }
        }

        private static void WriteScanSummary(string path, List<ScanSummary> scanSummaries)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tMass\tMedianPpmError\tScanEnvelopeIntensity\tMaxChargeEnvelopeIntensity\tBestCharge\tBestChargeCosine\tMinCharge\tMaxCharge\tChargeCount\tEvidenceCount");

                foreach (ScanSummary s in scanSummaries.OrderBy(s => s.Mass).ThenBy(s => s.Scan))
                {
                    writer.WriteLine(
                        s.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        s.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        s.Mass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        s.MedianPpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                        s.ScanEnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        s.MaxChargeEnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        s.BestCharge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        s.BestChargeCosine.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        s.MinCharge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        s.MaxCharge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        s.ChargeCount.ToString(CultureInfo.InvariantCulture) + "\t" +
                        s.EvidenceCount.ToString(CultureInfo.InvariantCulture)
                    );
                }
            }
        }

        private static void WriteFeatureSummary(string path, List<FeatureGroup> features)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("FeatureIndex\tMonoisotopicMass\tStartRetentionTime\tEndRetentionTime\tApexRetentionTime\tSumIntensity\tMaxScanIntensity\tMinCharge\tMaxCharge\tChargeCount\tMatchedScans\tMedianPpmError");

                foreach (FeatureGroup f in features)
                    WriteFeatureLine(writer, f, "");
            }
        }

        private static void WriteRejectedFeatureSummary(string path, List<FeatureGroup> features)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("FeatureIndex\tMonoisotopicMass\tStartRetentionTime\tEndRetentionTime\tApexRetentionTime\tSumIntensity\tMaxScanIntensity\tMinCharge\tMaxCharge\tChargeCount\tMatchedScans\tMedianPpmError\tRejectReason");

                foreach (FeatureGroup f in features)
                    WriteFeatureLine(writer, f, f.RejectReason);
            }
        }

        private static void WriteFeatureLine(StreamWriter writer, FeatureGroup f, string rejectReason)
        {
            List<ScanSummary> scans = f.Scans.OrderBy(s => s.Scan).ToList();

            if (scans.Count == 0)
                return;

            double sumIntensity = scans.Sum(s => s.ScanEnvelopeIntensity);
            ScanSummary apex = scans.OrderByDescending(s => s.ScanEnvelopeIntensity).First();

            int minCharge = scans.Min(s => s.MinCharge);
            int maxCharge = scans.Max(s => s.MaxCharge);

            List<int> allCharges = new List<int>();
            foreach (ScanSummary s in scans)
            {
                for (int z = s.MinCharge; z <= s.MaxCharge; z++)
                    allCharges.Add(z);
            }

            int chargeCount = allCharges.Distinct().Count();
            double medianPpm = Median(scans.Select(s => s.MedianPpmError).ToList());

            string line =
                f.FeatureIndex.ToString(CultureInfo.InvariantCulture) + "\t" +
                f.Mass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                scans.First().Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                scans.Last().Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                apex.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                sumIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                apex.ScanEnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                minCharge.ToString(CultureInfo.InvariantCulture) + "\t" +
                maxCharge.ToString(CultureInfo.InvariantCulture) + "\t" +
                chargeCount.ToString(CultureInfo.InvariantCulture) + "\t" +
                scans.Count.ToString(CultureInfo.InvariantCulture) + "\t" +
                medianPpm.ToString("F4", CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(rejectReason))
                line += "\t" + rejectReason;

            writer.WriteLine(line);
        }

        private static double Cosine(double[] observed, double[] theoretical)
        {
            double dot = 0.0;
            double a = 0.0;
            double b = 0.0;

            int n = Math.Min(observed.Length, theoretical.Length);

            for (int i = 0; i < n; i++)
            {
                dot += observed[i] * theoretical[i];
                a += observed[i] * observed[i];
                b += theoretical[i] * theoretical[i];
            }

            if (a <= 0.0 || b <= 0.0)
                return 0.0;

            return dot / Math.Sqrt(a * b);
        }

        private static double PpmError(double observed, double expected)
        {
            if (expected == 0.0 || double.IsNaN(observed) || double.IsNaN(expected))
                return double.NaN;

            return (observed - expected) / expected * 1e6;
        }

        private static double Median(List<double> values)
        {
            List<double> clean = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();

            if (clean.Count == 0)
                return double.NaN;

            int mid = clean.Count / 2;

            if (clean.Count % 2 == 1)
                return clean[mid];

            return 0.5 * (clean[mid - 1] + clean[mid]);
        }

        private static void PrintProgress(int percent)
        {
            int barWidth = 10;
            int filled = (percent * barWidth) / 100;
            Console.Write("\rProgress: [" + new string('#', filled) + new string('-', barWidth - filled) + "] " + percent.ToString().PadLeft(3) + "%");
        }
    }
}
