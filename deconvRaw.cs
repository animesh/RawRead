// targeted deconvolution-lite for Thermo RAW
// compile:
// mcs deconvRaw.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:deconvRaw.exe
// run:
// mono deconvRaw.exe file.raw targets.csv
// mono deconvRaw.exe "250924_rabina_16.raw" "targeted peptides inkl mito sched.csv"
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

        private class Hit
        {
            public int Scan;
            public double Rt;
            public string TargetName;
            public double TargetMass;
            public double ObservedMass;
            public double ObservedMz;
            public double PpmError;
            public int Charge;
            public double Intensity;
            public double BasePeakMass;
            public double Tic;
        }

        static void Main(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
            {
                Console.WriteLine("USAGE: {0} file.raw targets.csv [ppmTolerance] [minIntensity]", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];
            string targetPath = args[1];

            double ppmTolerance = 10.0;
            double minIntensity = 0.0;

            if (args.Length >= 3)
            {
                double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ppmTolerance);
            }

            if (args.Length >= 4)
            {
                double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out minIntensity);
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

            var hits = new List<Hit>();
            int lastPercent = -1;

            for (int scanNumber = firstScan; scanNumber <= lastScan; scanNumber++)
            {
                double rt = rawFile.RetentionTimeFromScanNumber(scanNumber);
                var scanStatistics = rawFile.GetScanStatsForScanNumber(scanNumber);

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
                    ProcessCentroidStream(scanNumber, rt, scanStatistics, centroidStream, targets, ppmTolerance, minIntensity, hits);
                }

                int processed = scanNumber - firstScan + 1;
                int percent = (int)((processed * 100L) / scanCount);

                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    int barWidth = 10;
                    int filled = (percent * barWidth) / 100;
                    Console.Write("\rProgress: [" + new string('#', filled) + new string('-', barWidth - filled) + "] " + percent.ToString().PadLeft(3) + "%");
                }
            }

            Console.WriteLine();
            rawFile.Dispose();

            string baseName = Path.GetFileName(targetPath);
            string evidenceFile = rawPath + "." + baseName + ".deconv_scan_evidence.tsv";
            string featureFile = rawPath + "." + baseName + ".deconv_features.tsv";
            string duplicateFile = rawPath + "." + baseName + ".deconv_duplicate_hits.tsv";

            WriteEvidence(evidenceFile, hits);
            WriteFeatureSummary(featureFile, hits);
            WriteDuplicateHits(duplicateFile, hits);

            Console.WriteLine("Wrote scan evidence: {0}", evidenceFile);
            Console.WriteLine("Wrote feature summary: {0}", featureFile);
            Console.WriteLine("Wrote duplicate hits: {0}", duplicateFile);
        }

        private static void ProcessCentroidStream(
            int scanNumber,
            double rt,
            dynamic scanStatistics,
            CentroidStream centroidStream,
            List<Target> targets,
            double ppmTolerance,
            double minIntensity,
            List<Hit> hits)
        {
            for (int j = 0; j < centroidStream.Length; j++)
            {
                double mz = centroidStream.Masses[j];
                double intensity = centroidStream.Intensities[j];

                if (intensity < minIntensity)
                {
                    continue;
                }

                int z = 0;

                try
                {
                    z = (int)Math.Round(centroidStream.Charges[j]);
                }
                catch
                {
                    z = 0;
                }

                foreach (Target target in targets)
                {
                    if (!double.IsNaN(target.StartMin) && rt < target.StartMin)
                    {
                        continue;
                    }

                    if (!double.IsNaN(target.EndMin) && rt > target.EndMin)
                    {
                        continue;
                    }

                    if (target.Charge > 0)
                    {
                        if (z > 0 && z != target.Charge)
                        {
                            continue;
                        }

                        double expectedMz = (target.NeutralMass + target.Charge * Proton) / target.Charge;
                        double ppmErrorMz = PpmError(mz, expectedMz);

                        if (Math.Abs(ppmErrorMz) <= ppmTolerance)
                        {
                            double observedMass = mz * target.Charge - target.Charge * Proton;

                            hits.Add(new Hit
                            {
                                Scan = scanNumber,
                                Rt = rt,
                                TargetName = target.Name,
                                TargetMass = target.NeutralMass,
                                ObservedMass = observedMass,
                                ObservedMz = mz,
                                PpmError = PpmError(observedMass, target.NeutralMass),
                                Charge = target.Charge,
                                Intensity = intensity,
                                BasePeakMass = scanStatistics.BasePeakMass,
                                Tic = scanStatistics.TIC
                            });
                        }

                        continue;
                    }

                    if (z <= 0)
                    {
                        continue;
                    }

                    if (z < target.MinCharge || z > target.MaxCharge)
                    {
                        continue;
                    }

                    double neutralMass = mz * z - z * Proton;
                    double ppmError = PpmError(neutralMass, target.NeutralMass);

                    if (Math.Abs(ppmError) <= ppmTolerance)
                    {
                        hits.Add(new Hit
                        {
                            Scan = scanNumber,
                            Rt = rt,
                            TargetName = target.Name,
                            TargetMass = target.NeutralMass,
                            ObservedMass = neutralMass,
                            ObservedMz = mz,
                            PpmError = ppmError,
                            Charge = z,
                            Intensity = intensity,
                            BasePeakMass = scanStatistics.BasePeakMass,
                            Tic = scanStatistics.TIC
                        });
                    }
                }
            }
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
                int neutralMassIdx = FindColumn(header, "Mass", "MonoisotopicMass", "NeutralMass");
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

                    var target = new Target();
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

        private static void WriteEvidence(string path, List<Hit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tTarget\tTargetMass\tObservedMass\tObservedMz\tPpmError\tCharge\tIntensity\tBasePeakMass\tTIC");

                foreach (Hit h in hits.OrderBy(h => h.TargetName).ThenBy(h => h.Rt).ThenBy(h => h.Charge))
                {
                    writer.WriteLine(
                        h.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.TargetName + "\t" +
                        h.TargetMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.ObservedMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.ObservedMz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        h.PpmError.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                        h.Charge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        h.Intensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        h.BasePeakMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        h.Tic.ToString("G17", CultureInfo.InvariantCulture)
                    );
                }
            }
        }

        private static void WriteFeatureSummary(string path, List<Hit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Target\tTargetMass\tMeanObservedMass\tMedianPpmError\tStartRetentionTime\tEndRetentionTime\tApexRetentionTime\tSumIntensity\tMaxIntensity\tMinCharge\tMaxCharge\tChargeCount\tMatchedScans");

                var groups = hits.GroupBy(h => new { h.TargetName, h.TargetMass });

                foreach (var group in groups.OrderBy(g => g.Key.TargetName))
                {
                    var list = group.OrderBy(h => h.Rt).ToList();

                    if (list.Count == 0)
                    {
                        continue;
                    }

                    Hit apex = list.OrderByDescending(h => h.Intensity).First();
                    var charges = list.Select(h => h.Charge).Distinct().OrderBy(z => z).ToList();

                    writer.WriteLine(
                        group.Key.TargetName + "\t" +
                        group.Key.TargetMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        list.Average(h => h.ObservedMass).ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        Median(list.Select(h => h.PpmError).ToList()).ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                        list.First().Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        list.Last().Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        apex.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        list.Sum(h => h.Intensity).ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        list.Max(h => h.Intensity).ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        charges.First().ToString(CultureInfo.InvariantCulture) + "\t" +
                        charges.Last().ToString(CultureInfo.InvariantCulture) + "\t" +
                        charges.Count.ToString(CultureInfo.InvariantCulture) + "\t" +
                        list.Select(h => h.Scan).Distinct().Count().ToString(CultureInfo.InvariantCulture)
                    );
                }
            }
        }

        private static void WriteDuplicateHits(string path, List<Hit> hits)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Scan\tRT\tObservedMz\tCharge\tIntensity\tMatchedTargetCount\tTargets");

                var groups = hits.GroupBy(h => new { h.Scan, h.ObservedMz, h.Charge });

                foreach (var group in groups.Where(g => g.Select(h => h.TargetName).Distinct().Count() > 1))
                {
                    Hit first = group.First();
                    string targets = string.Join(";", group.Select(h => h.TargetName).Distinct().OrderBy(s => s));

                    writer.WriteLine(
                        first.Scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                        first.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        first.ObservedMz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                        first.Charge.ToString(CultureInfo.InvariantCulture) + "\t" +
                        first.Intensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        group.Select(h => h.TargetName).Distinct().Count().ToString(CultureInfo.InvariantCulture) + "\t" +
                        targets
                    );
                }
            }
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
    }
}