// mcs countIons.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:System.Numerics.dll -out:countIons.exe
// mono countIons.exe "250924_rabina_16.raw" "targeted peptides inkl mito sched.csv"
// mono countIons.exe file.raw targets.csv
// mono countIons.exe file.raw targets.csv 100000 2

using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.Data.Business;

namespace CountIons
{
    internal class CountIonsFixed
    {
        private const double DefaultMzToleranceDa = 0.0001;
        private const double DefaultTimeSlackMin = 0.01;

        private class Target
        {
            public string[] Fields;
            public double MassMz;
            public double StartMin;
            public double EndMin;
            public double AccumulatedTic;
            public int MatchedCount;
            public List<string> MatchedScans = new List<string>();
            public List<double> MatchedTimes = new List<double>();

            public Target(string[] fields, double massMz, double startMin, double endMin)
            {
                Fields = fields;
                MassMz = massMz;
                StartMin = startMin;
                EndMin = endMin;
            }
        }

        private class CiRow
        {
            public string Scan;
            public double BasePeakMass;
            public double Tic;
            public double MaxIntSum;
            public string Title;
            public double MaxMass;
            public double Time;
            public double MaxInt;
            public string Charge;
            public string OriginalLine;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("USAGE: {0} <raw-file> [targets.csv] [intensityThreshold] [chargeThreshold]", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];
            string csvPath = null;
            double intensityThreshold = 0.0;
            int chargeThreshold = 0;

            int argIndex = 1;

            if (args.Length > argIndex && File.Exists(args[argIndex]))
            {
                csvPath = args[argIndex];
                argIndex++;
            }

            if (args.Length > argIndex)
            {
                double.TryParse(args[argIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out intensityThreshold);
                argIndex++;
            }

            if (args.Length > argIndex)
            {
                int.TryParse(args[argIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out chargeThreshold);
                argIndex++;
            }

            var rawFile = RawFileReaderAdapter.FileFactory(rawPath);

            if (!rawFile.IsOpen || rawFile.IsError)
            {
                Console.Error.WriteLine("Error opening raw file: {0} FileError: {1}", rawPath, rawFile.FileError);
                return;
            }

            rawFile.SelectInstrument(Device.MS, 1);

            string rawFileName = rawFile.FileName;
            int firstScan = rawFile.RunHeaderEx.FirstSpectrum;
            int lastScan = rawFile.RunHeaderEx.LastSpectrum;
            int scanCount = lastScan - firstScan + 1;
            string ciFileName = rawFileName + ".cI.tsv";

            Console.WriteLine("#filename:\t{0}", rawFileName);
            Console.WriteLine("#prescan(s):\t{0}", scanCount);
            Console.WriteLine("#RT length:\t{0}", rawFile.RunHeaderEx.EndTime - rawFile.RunHeaderEx.StartTime);

            bool ciComplete = IsCiFileComplete(ciFileName, scanCount);

            if (!ciComplete)
            {
                WriteCompactScanTable(rawFile, ciFileName, firstScan, lastScan, intensityThreshold, chargeThreshold);
            }
            else
            {
                Console.WriteLine("Found existing complete cI.tsv: {0}", ciFileName);
            }

            rawFile.Dispose();

            if (string.IsNullOrEmpty(csvPath))
            {
                Console.WriteLine("No target CSV supplied; wrote/used compact scan table only.");
                return;
            }

            string[] csvHeader;
            List<Target> targets = ReadTargets(csvPath, out csvHeader);

            if (targets.Count == 0)
            {
                Console.Error.WriteLine("No valid targets found in {0}", csvPath);
                return;
            }

            List<CiRow> ciRows = ReadCiRows(ciFileName);
            AccumulateTargets(ciRows, targets, DefaultMzToleranceDa, DefaultTimeSlackMin);

            string csvBase = Path.GetFileName(csvPath);
            string accFile = rawFileName + "." + csvBase + ".accumulation.csv";
            string dupFile = rawFileName + "." + csvBase + ".duplicated_scans.tsv";
            string unmatchedFile = rawFileName + "." + csvBase + ".unmatched_scans.tsv";

            WriteAccumulationCsv(accFile, csvHeader, targets);
            WriteMatchReports(dupFile, unmatchedFile, ciRows, targets);

            Console.WriteLine("Wrote targeted accumulation: {0}", accFile);
            Console.WriteLine("Wrote duplicated scan report: {0}", dupFile);
            Console.WriteLine("Wrote unmatched scan report: {0}", unmatchedFile);
        }

        private static void WriteCompactScanTable(dynamic rawFile, string ciFileName, int firstScan, int lastScan, double intensityThreshold, int chargeThreshold)
        {
            int scanCount = lastScan - firstScan + 1;
            int lastPercent = -1;
            double totalTic = 0.0;
            double maxIntSum = 0.0;
            int ionCount = 0;

            using (var writer = new StreamWriter(ciFileName))
            {
                writer.WriteLine("#prescan(s):\t" + scanCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("scan\tBasePeakMass\tTIC\tmaxIntSum\ttitle\tmaxMass\ttime\tmaxInt\tcharge");

                for (int scanNumber = firstScan; scanNumber <= lastScan; scanNumber++)
                {
                    double time = rawFile.RetentionTimeFromScanNumber(scanNumber);
                    string title = SafeOneLine(string.Join(" ", rawFile.GetScanEventForScanNumber(scanNumber)));
                    var scanStatistics = rawFile.GetScanStatsForScanNumber(scanNumber);

                    double basePeakMass = scanStatistics.BasePeakMass;
                    double tic = scanStatistics.TIC;
                    double maxMass = scanStatistics.BasePeakMass;
                    double maxInt = scanStatistics.BasePeakIntensity;
                    string charge = GetTrailerValue(rawFile, scanNumber, "Charge State:");

                    maxIntSum += maxInt;
                    totalTic += tic;

                    int parsedCharge = 0;
                    int.TryParse(charge, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedCharge);

                    if (maxInt >= intensityThreshold && (chargeThreshold == 0 || parsedCharge >= chargeThreshold))
                    {
                        ionCount++;
                    }

                    writer.WriteLine(
                        scanNumber.ToString(CultureInfo.InvariantCulture) + "\t" +
                        basePeakMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        tic.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        maxIntSum.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        title + "\t" +
                        maxMass.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        time.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        maxInt.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                        charge
                    );

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
            }

            Console.WriteLine();
            Console.WriteLine("#TIC>={0} intensity:\t{1}", intensityThreshold.ToString(CultureInfo.InvariantCulture), totalTic.ToString("G17", CultureInfo.InvariantCulture));
            Console.WriteLine("#Ions>=charge{0}:\t{1}", chargeThreshold.ToString(CultureInfo.InvariantCulture), ionCount.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Wrote: {0}", ciFileName);
        }

        private static bool IsCiFileComplete(string ciFileName, int expectedRows)
        {
            if (!File.Exists(ciFileName))
            {
                return false;
            }

            int rows = 0;

            foreach (string line in File.ReadLines(ciFileName))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("#"))
                {
                    continue;
                }

                if (line.StartsWith("scan\t", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows++;
            }

            return rows == expectedRows;
        }

        private static List<Target> ReadTargets(string csvPath, out string[] header)
        {
            var targets = new List<Target>();
            header = null;

            using (var reader = new StreamReader(csvPath))
            {
                string headerLine = reader.ReadLine();

                if (headerLine == null)
                {
                    return targets;
                }

                header = ParseCsvLine(headerLine).ToArray();

                int massIdx = FindColumn(header, "Mass [m/z]", "mz", "m/z", "Mass");
                int startIdx = FindColumn(header, "Start [min]", "StartMin", "Start");
                int endIdx = FindColumn(header, "End [min]", "EndMin", "End");

                string line;

                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] fields = ParseCsvLine(line).ToArray();

                    double mass = GetDouble(fields, massIdx);
                    double start = GetDouble(fields, startIdx);
                    double end = GetDouble(fields, endIdx);

                    if (double.IsNaN(mass))
                    {
                        continue;
                    }

                    targets.Add(new Target(fields, mass, start, end));
                }
            }

            return targets;
        }

        private static List<CiRow> ReadCiRows(string ciFileName)
        {
            var rows = new List<CiRow>();

            foreach (string line in File.ReadLines(ciFileName))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("scan\t", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] p = line.Split('\t');

                if (p.Length < 9)
                {
                    continue;
                }

                rows.Add(new CiRow
                {
                    Scan = p[0],
                    BasePeakMass = GetDouble(p, 1),
                    Tic = GetDouble(p, 2),
                    MaxIntSum = GetDouble(p, 3),
                    Title = p[4],
                    MaxMass = GetDouble(p, 5),
                    Time = GetDouble(p, 6),
                    MaxInt = GetDouble(p, 7),
                    Charge = p[8],
                    OriginalLine = line
                });
            }

            return rows;
        }

        private static void AccumulateTargets(List<CiRow> rows, List<Target> targets, double toleranceDa, double timeSlackMin)
        {
            foreach (CiRow row in rows)
            {
                double observedMz = ExtractMzFromTitle(row.Title);

                if (double.IsNaN(observedMz))
                {
                    continue;
                }

                foreach (Target target in targets)
                {
                    bool inWindow = true;

                    if (!double.IsNaN(target.StartMin) && row.Time < target.StartMin - timeSlackMin)
                    {
                        inWindow = false;
                    }

                    if (!double.IsNaN(target.EndMin) && row.Time > target.EndMin + timeSlackMin)
                    {
                        inWindow = false;
                    }

                    if (!inWindow)
                    {
                        continue;
                    }

                    if (Math.Abs(observedMz - target.MassMz) <= toleranceDa)
                    {
                        target.AccumulatedTic += row.Tic;
                        target.MatchedCount++;
                        target.MatchedScans.Add(row.Scan);
                        target.MatchedTimes.Add(row.Time);
                    }
                }
            }
        }

        private static void WriteAccumulationCsv(string path, string[] header, List<Target> targets)
        {
            using (var writer = new StreamWriter(path))
            {
                if (header != null && header.Length > 0)
                {
                    writer.Write(string.Join(",", header.Select(EscapeCsv)));
                }
                else
                {
                    writer.Write("Compound,Mass [m/z]");
                }

                writer.WriteLine(",AccumulatedTIC,MatchedCount,MatchedScans,MatchedTimes");

                foreach (Target target in targets)
                {
                    writer.Write(string.Join(",", target.Fields.Select(EscapeCsv)));
                    writer.Write(",");
                    writer.Write(target.AccumulatedTic.ToString("G17", CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(target.MatchedCount.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(EscapeCsv(string.Join(";", target.MatchedScans)));
                    writer.Write(",");
                    writer.WriteLine(EscapeCsv(string.Join(";", target.MatchedTimes.Select(v => v.ToString("G17", CultureInfo.InvariantCulture)))));
                }
            }
        }

        private static void WriteMatchReports(string dupFile, string unmatchedFile, List<CiRow> ciRows, List<Target> targets)
        {
            var scanToTargets = new Dictionary<string, List<int>>();

            for (int i = 0; i < targets.Count; i++)
            {
                foreach (string scan in targets[i].MatchedScans)
                {
                    if (!scanToTargets.ContainsKey(scan))
                    {
                        scanToTargets[scan] = new List<int>();
                    }

                    scanToTargets[scan].Add(i);
                }
            }

            using (var writer = new StreamWriter(dupFile))
            {
                writer.WriteLine("scan\tmatch_count\tci_row\tmatched_target_rows");

                foreach (var kv in scanToTargets.Where(kv => kv.Value.Count > 1).OrderBy(kv => SafeInt(kv.Key)))
                {
                    string ciRow = ciRows.FirstOrDefault(r => r.Scan == kv.Key) != null ? ciRows.First(r => r.Scan == kv.Key).OriginalLine : "";
                    string targetRows = string.Join(" || ", kv.Value.Select(i => string.Join(",", targets[i].Fields)));
                    writer.WriteLine(kv.Key + "\t" + kv.Value.Count.ToString(CultureInfo.InvariantCulture) + "\t" + ciRow + "\t" + targetRows);
                }
            }

            using (var writer = new StreamWriter(unmatchedFile))
            {
                foreach (CiRow row in ciRows)
                {
                    if (!scanToTargets.ContainsKey(row.Scan))
                    {
                        writer.WriteLine(row.OriginalLine);
                    }
                }
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

        private static double ExtractMzFromTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return double.NaN;
            }

            Match match = Regex.Match(title, "(\\d+\\.\\d+)(?=@)");

            if (!match.Success)
            {
                match = Regex.Match(title, "(\\d+\\.\\d+)");
            }

            if (!match.Success)
            {
                return double.NaN;
            }

            double value;

            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }

            return double.NaN;
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

        private static int SafeInt(string value)
        {
            int output;

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out output))
            {
                return output;
            }

            return int.MaxValue;
        }

        private static string SafeOneLine(string text)
        {
            if (text == null)
            {
                return "";
            }

            return text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        }

        private static string EscapeCsv(string s)
        {
            if (s == null)
            {
                return "";
            }

            if (s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }

            return s;
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