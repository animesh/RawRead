// targeted isotope-envelope deconvolution-lite for Thermo RAW
// released under GPL version 2 or later: sharma.animesh@gmail.com
//
// compile:
// mcs deconvRaw.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:deconvRaw.exe
//
// run:
// mono deconvRaw.exe 260629_Solveig_3_L.raw deconvTarget.csv 10 0 0.70 3
//
// targets.csv accepted columns:
// Compound or Name or Target
// Mass or MonoisotopicMass or NeutralMass
// Mass [m/z] or mz or m/z
// Charge
// MinCharge
// MaxCharge
// Start [min]
// End [min]

using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.Data.Business;

namespace DeconvRaw
{
    internal class DeconvRaw
    {
        private const double Proton = 1.007276466812;
        private const double C13MinusC12 = 1.00335483507;

        private class Target
        {
            public string Name;
            public string[] Fields;
            public double NeutralMass;
            public double Mz;
            public int Charge;
            public int MinCharge;
            public int MaxCharge;
            public double StartMin;
            public double EndMin;
        }

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
            public string TargetName;
            public double TargetMass;
            public double ObservedMass;
            public double PpmError;
            public int Charge;
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

        static void Main(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
            {
                Console.WriteLine("USAGE: {0} file.raw targets.csv [ppmTolerance=10] [minEnvelopeIntensity=0] [minCos=0.70] [minMatchedIsotopes=3]", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];
            string targetPath = args[1];

            double ppmTolerance = 10.0;
            double minEnvelopeIntensity = 0.0;
            double minCos = 0.70;
            int minMatchedIsotopes = 3;

            if (args.Length >= 3)
            {
                double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ppmTolerance);
            }

            if (args.Length >= 4)
            {
                double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out minEnvelopeIntensity);
            }

            if (args.Length >= 5)
            {
                double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out minCos);
            }

            if (args.Length >= 6)
            {
                int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out minMatchedIsotopes);
            }

            string[] targetHeader;
            List<Target> targets = ReadTargets(targetPath, out targetHeader);

            if (targets.Count == 0)
            {
                Console.Error.WriteLine("No valid targets found in {0}", targetPath);
                return;
            }

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
            Console.WriteLine("#targets:\t{0}", targets.Count.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#ppmTolerance:\t{0}", ppmTolerance.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minEnvelopeIntensity:\t{0}", minEnvelopeIntensity.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minCos:\t{0}", minCos.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minMatchedIsotopes:\t{0}", minMatchedIsotopes.ToString(CultureInfo.InvariantCulture));

            var hits = new List<EnvelopeHit>();
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

                bool looksLikeMs1 = scanEventText.IndexOf(" ms ", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                    scanEventText.IndexOf("@", StringComparison.OrdinalIgnoreCase) < 0;

                if (!looksLikeMs1)
                {
                    int processedSkip = scanNumber - firstScan + 1;
                    int percentSkip = (int)((processedSkip * 100L) / scanCount);

                    if (percentSkip != lastPercent)
                    {
                        lastPercent = percentSkip;
                        PrintProgress(percentSkip);
                    }

                    continue;
                }

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
                    double[] mzArray = centroidStream.Masses;
                    double[] intensityArray = centroidStream.Intensities;

                    for (int ti = 0; ti < targets.Count; ti++)
                    {
                        Target target = targets[ti];

                        if (!double.IsNaN(target.StartMin) && rt < target.StartMin)
                        {
                            continue;
                        }

                        if (!double.IsNaN(target.EndMin) && rt > target.EndMin)
                        {
                            continue;
                        }

                        int minCharge;
                        int maxCharge;

                        if (target.Charge > 0)
                        {
                            minCharge = target.Charge;
                            maxCharge = target.Charge;
                        }
                        else
                        {
                            minCharge = target.MinCharge;
                            maxCharge = target.MaxCharge;
                        }

                        if (minCharge <= 0)
                        {
                            minCharge = 1;
                        }

                        if (maxCharge < minCharge)
                        {
                            maxCharge = minCharge;
                        }

                        for (int z = minCharge; z <= maxCharge; z++)
                        {
                            EnvelopeHit hit = TryMatchEnvelope(
                                scanNumber,
                                rt,
                                scanStatistics,
                                target,
                                z,
                                mzArray,
                                intensityArray,
                                ppmTolerance,
                                minEnvelopeIntensity,
                                minCos,
                                minMatchedIsotopes
                            );

                            if (hit != null)
                            {
                                hits.Add(hit);
                            }
                        }
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

            string baseName = Path.GetFileName(targetPath);
            string evidenceFile = rawPath + "." + baseName + ".deconv_scan_evidence.tsv";
            string featureFile = rawPath + "." + baseName + ".deconv_features.tsv";
            string duplicateFile = rawPath + "." + baseName + ".deconv_duplicate_hits.tsv";
            string scanSummaryFile = rawPath + "." + baseName + ".deconv_scan_summary.tsv";

            WriteEvidence(evidenceFile, hits);
            WriteScanSummary(scanSummaryFile, hits);
            WriteFeatureSummary(featureFile, hits);
            WriteDuplicateHits(duplicateFile, hits);

            Console.WriteLine("Wrote scan evidence: {0}", evidenceFile);
            Console.WriteLine("Wrote scan summary: {0}", scanSummaryFile);
            Console.WriteLine("Wrote feature summary: {0}", featureFile);
            Console.WriteLine("Wrote duplicate hits: {0}", duplicateFile);
        }

        private static EnvelopeHit TryMatchEnvelope(
            int scanNumber,
            double rt,
            dynamic scanStatistics,
            Target target,
            int charge,
            double[] mzArray,
            double[] intensityArray,
            double ppmTolerance,
            double minEnvelopeIntensity,
            double minCos,
            int minMatchedIsotopes)
        {
            if (charge <= 0)
            {
                return null;
            }

            int isotopeCount = EstimateIsotopeCount(target.NeutralMass);
            double[] theoretical = BuildPoissonEnvelope(target.NeutralMass, isotopeCount);
            double[] observed = new double[isotopeCount];

            double envelopeIntensity = 0.0;
            double maxIsotopeIntensity = 0.0;
            int apexIsotopeIndex = -1;
            int matchedIsotopeCount = 0;

            double observedMassWeightedSum = 0.0;
            double observedMassWeight = 0.0;

            for (int isotopeIndex = 0; isotopeIndex < isotopeCount; isotopeIndex++)
            {
                double expectedMz = (target.NeutralMass + isotopeIndex * C13MinusC12 + charge * Proton) / charge;
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
            {
                return null;
            }

            if (envelopeIntensity < minEnvelopeIntensity)
            {
                return null;
            }

            double cosine = Cosine(observed, theoretical);

            if (cosine < minCos)
            {
                return null;
            }

            double observedMass = observedMassWeight > 0.0 ? observedMassWeightedSum / observedMassWeight : double.NaN;
            double ppmError = PpmError(observedMass, target.NeutralMass);
            double baseMz = (target.NeutralMass + charge * Proton) / charge;

            return new EnvelopeHit
            {
                Scan = scanNumber,
                Rt = rt,
                TargetName = target.Name,
                TargetMass = target.NeutralMass,
                ObservedMass = observedMass,
                PpmError = ppmError,
                Charge = charge,
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

        private static int EstimateIsotopeCount(double neutralMass)
        {
            double lambda = EstimateCarbonLambda(neutralMass);
            int count = (int)Math.Ceiling(lambda + 8.0 * Math.Sqrt(lambda) + 10.0);

            if (count < 16)
            {
                count = 16;
            }

            if (count > 120)
            {
                count = 120;
            }

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
            {
                envelope[i] = envelope[i - 1] * lambda / i;
            }

            double sum = envelope.Sum();

            if (sum > 0.0)
            {
                for (int i = 0; i < envelope.Length; i++)
                {
                    envelope[i] /= sum;
                }
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
                {
                    break;
                }

                if (mzArray[i] < minMz)
                {
                    continue;
                }

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
                {
                    left = mid + 1;
                }
                else
                {
                    right = mid;
                }
            }

            return left;
        }

        private static List<Target> ReadTargets(string path, out string[] header)
        {
            var targets = new List<Target>();
            header = null;

            using (var reader = new StreamReader(path))
            {
                string headerLine = reader.ReadLine();

                if (headerLine == null)
                {
                    return targets;
                }

                header = ParseCsvLine(headerLine).ToArray();

                int nameIdx = FindColumn(header, "Compound", "Name", "Target");
                int neutralMassIdx = FindColumn(header, "Mass", "MonoisotopicMass", "NeutralMass", "TargetMass");
                int mzIdx = FindColumn(header, "Mass [m/z]", "mz", "m/z");
                int chargeIdx = FindColumn(header, "Charge", "z");
                int minChargeIdx = FindColumn(header, "MinCharge", "Min Charge");
                int maxChargeIdx = FindColumn(header, "MaxCharge", "Max Charge");
                int startIdx = FindColumn(header, "Start [min]", "StartMin", "Start");
                int endIdx = FindColumn(header, "End [min]", "EndMin", "End");

                string line;
                int lineNumber = 1;

                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] fields = ParseCsvLine(line).ToArray();

                    Target target = new Target();
                    target.Fields = fields;
                    target.Name = GetString(fields, nameIdx);

                    if (string.IsNullOrWhiteSpace(target.Name))
                    {
                        target.Name = "target_" + lineNumber.ToString(CultureInfo.InvariantCulture);
                    }

                    target.NeutralMass = GetDouble(fields, neutralMassIdx);
                    target.Mz = GetDouble(fields, mzIdx);
                    target.Charge = GetInt(fields, chargeIdx);
                    target.MinCharge = GetInt(fields, minChargeIdx);
                    target.MaxCharge = GetInt(fields, maxChargeIdx);
                    target.StartMin = GetDouble(fields, startIdx);
                    target.EndMin = GetDouble(fields, endIdx);

                    if (target.MinCharge <= 0)
                    {
                        target.MinCharge = 1;
                    }

                    if (target.MaxCharge <= 0)
                    {
                        target.MaxCharge = 100;
                    }

                    if (double.IsNaN(target.NeutralMass) && !double.IsNaN(target.Mz) && target.Charge > 0)
                    {
                        target.NeutralMass = target.Mz * target.Charge - target.Charge * Proton;
                    }

                    if (!double.IsNaN(target.NeutralMass))
                    {
                        targets.Add(target);
                    }
                }
            }

            return targets;
        }

        private static void WriteEvidence(string path, List<EnvelopeHit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tTarget\tTargetMass\tObservedMass\tPpmError\tCharge\tBaseMz\tEnvelopeIntensity\tMaxIsotopeIntensity\tApexIsotopeIndex\tMatchedIsotopeCount\tTotalIsotopeCount\tIsotopeCosineScore\tBasePeakMass\tTIC");

                foreach (EnvelopeHit h in hits.OrderBy(h => h.TargetName).ThenBy(h => h.Rt).ThenBy(h => h.Charge))
                {
                    writer.WriteLine(
                        h.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.TargetName + "\t" +
                        h.TargetMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.ObservedMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.PpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                        h.Charge.ToString(CultureInfo.InvariantCulture) + "\t" +
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

        private static void WriteScanSummary(string path, List<EnvelopeHit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Target\tTargetMass\tScan\tRT\tScanEnvelopeIntensity\tBestCharge\tBestChargeEnvelopeIntensity\tBestChargeCosine\tChargeCount\tMinCharge\tMaxCharge\tWeightedObservedMass\tWeightedPpmError");

                var groups = hits.GroupBy(h => new { h.TargetName, h.TargetMass, h.Scan });

                foreach (var group in groups.OrderBy(g => g.Key.TargetName).ThenBy(g => g.First().Rt))
                {
                    var list = group.ToList();
                    EnvelopeHit best = list.OrderByDescending(h => h.EnvelopeIntensity).First();

                    double sumI = list.Sum(h => h.EnvelopeIntensity);
                    double weightedMass = sumI > 0.0 ? list.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / sumI : double.NaN;
                    double weightedPpm = PpmError(weightedMass, group.Key.TargetMass);
                    var charges = list.Select(h => h.Charge).Distinct().OrderBy(z => z).ToList();

                    writer.WriteLine(
                        group.Key.TargetName + "\t" +
                        group.Key.TargetMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        group.Key.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        best.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        sumI.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        best.Charge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        best.EnvelopeIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        best.IsotopeCosineScore.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        charges.Count.ToString(CultureInfo.InvariantCulture) + "\t" +
                        charges.First().ToString(CultureInfo.InvariantCulture) + "\t" +
                        charges.Last().ToString(CultureInfo.InvariantCulture) + "\t" +
                        weightedMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        weightedPpm.ToString("F4", CultureInfo.InvariantCulture)
                    );
                }
            }
        }

        private static void WriteFeatureSummary(string path, List<EnvelopeHit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Target\tTargetMass\tMeanObservedMass\tMedianPpmError\tStartRetentionTime\tEndRetentionTime\tApexRetentionTime\tSumIntensity\tMaxScanIntensity\tMinCharge\tMaxCharge\tChargeCount\tMatchedScans\tMedianIsotopeCosineScore\tBestIsotopeCosineScore");

                var groups = hits.GroupBy(h => new { h.TargetName, h.TargetMass });

                foreach (var group in groups.OrderBy(g => g.Key.TargetName))
                {
                    var list = group.OrderBy(h => h.Rt).ToList();

                    if (list.Count == 0)
                    {
                        continue;
                    }

                    var scanGroups = list.GroupBy(h => h.Scan)
                                         .Select(g => new
                                         {
                                             Scan = g.Key,
                                             Rt = g.First().Rt,
                                             ScanIntensity = g.Sum(h => h.EnvelopeIntensity)
                                         })
                                         .OrderBy(g => g.Rt)
                                         .ToList();

                    var apexScan = scanGroups.OrderByDescending(g => g.ScanIntensity).First();

                    double totalIntensity = list.Sum(h => h.EnvelopeIntensity);
                    double meanObservedMass = totalIntensity > 0.0 ? list.Sum(h => h.ObservedMass * h.EnvelopeIntensity) / totalIntensity : double.NaN;
                    double medianPpm = Median(list.Select(h => h.PpmError).ToList());
                    double medianCos = Median(list.Select(h => h.IsotopeCosineScore).ToList());
                    double bestCos = list.Max(h => h.IsotopeCosineScore);

                    var charges = list.Select(h => h.Charge).Distinct().OrderBy(z => z).ToList();

                    writer.WriteLine(
                        group.Key.TargetName + "\t" +
                        group.Key.TargetMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        meanObservedMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        medianPpm.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                        scanGroups.First().Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        scanGroups.Last().Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        apexScan.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        totalIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        apexScan.ScanIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        charges.First().ToString(CultureInfo.InvariantCulture) + "\t" +
                        charges.Last().ToString(CultureInfo.InvariantCulture) + "\t" +
                        charges.Count.ToString(CultureInfo.InvariantCulture) + "\t" +
                        scanGroups.Count.ToString(CultureInfo.InvariantCulture) + "\t" +
                        medianCos.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        bestCos.ToString("F6", CultureInfo.InvariantCulture)
                    );
                }
            }
        }

        private static void WriteDuplicateHits(string path, List<EnvelopeHit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tCharge\tMatchedTargetCount\tTargets");

                var groups = hits.GroupBy(h => new { h.Scan, h.Charge });

                foreach (var group in groups.Where(g => g.Select(h => h.TargetName).Distinct().Count() > 1))
                {
                    EnvelopeHit first = group.First();
                    string targets = string.Join(";", group.Select(h => h.TargetName).Distinct().OrderBy(s => s));

                    writer.WriteLine(
                        first.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        first.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        first.Charge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        group.Select(h => h.TargetName).Distinct().Count().ToString(CultureInfo.InvariantCulture) + "\t" +
                        targets
                    );
                }
            }
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
            {
                return 0.0;
            }

            return dot / Math.Sqrt(a * b);
        }

        private static double PpmError(double observed, double expected)
        {
            if (expected == 0.0 || double.IsNaN(observed) || double.IsNaN(expected))
            {
                return double.NaN;
            }

            return (observed - expected) / expected * 1e6;
        }

        private static double Median(List<double> values)
        {
            var clean = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();

            if (clean.Count == 0)
            {
                return double.NaN;
            }

            int mid = clean.Count / 2;

            if (clean.Count % 2 == 1)
            {
                return clean[mid];
            }

            return 0.5 * (clean[mid - 1] + clean[mid]);
        }

        private static int FindColumn(string[] header, params string[] names)
        {
            if (header == null)
            {
                return -1;
            }

            for (int i = 0; i < header.Length; i++)
            {
                string h = header[i].Trim();

                foreach (string name in names)
                {
                    if (h.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string GetString(string[] fields, int index)
        {
            if (index < 0 || index >= fields.Length)
            {
                return "";
            }

            return fields[index];
        }

        private static double GetDouble(string[] fields, int index)
        {
            if (index < 0 || index >= fields.Length)
            {
                return double.NaN;
            }

            double value;

            if (double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return double.NaN;
        }

        private static int GetInt(string[] fields, int index)
        {
            if (index < 0 || index >= fields.Length)
            {
                return 0;
            }

            int value;

            if (int.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return 0;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var output = new List<string>();

            if (line == null)
            {
                return output;
            }

            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    output.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(c);
                }
            }

            output.Add(current.ToString());
            return output;
        }

        private static void PrintProgress(int percent)
        {
            int barWidth = 10;
            int filled = (percent * barWidth) / 100;
            Console.Write("\rProgress: [" + new string('#', filled) + new string('-', barWidth - filled) + "] " + percent.ToString().PadLeft(3) + "%");
        }
    }
}