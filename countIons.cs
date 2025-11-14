// mcs countIons.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll 
// mono countIons.exe "250924_rabina_16.raw" "targeted peptides inkl mito sched.csv"
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.Data.Business;

namespace countIons
{
    internal class countIons2PeakList
    {
        private class Target
        {
            public string[] Fields;
            public double Mass;
            public double AccTIC;
            public int MatchedCount;
            public List<string> MatchedMasses;
            public double AccTime;
            public List<double> MatchedTimes;
            public double StartMin;
            public double EndMin;
            public Target(string[] fields, double mass)
            {
                Fields = fields; Mass = mass; AccTIC = 0.0; MatchedCount = 0;
                MatchedMasses = new List<string>(); AccTime = 0.0; MatchedTimes = new List<double>();
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("USAGE: {0} <raw-file> [targets.csv] [intensityThreshold] [chargeThreshold]", AppDomain.CurrentDomain.FriendlyName); return; }

            var rawFile = RawFileReaderAdapter.FileFactory(args[0]);
            if (!rawFile.IsOpen || rawFile.IsError) { Console.Error.WriteLine("Error opening raw file: {0} (FileError: {1})", args[0], rawFile.FileError); return; }

            string rawFileName;
            try { rawFileName = rawFile.FileName; } catch (Exception ex) { Console.Error.WriteLine("Error reading raw metadata: {0}", ex.Message); rawFile.Dispose(); return; }

            double insThr = 0; int chgThr = 0; string csvArg = null; int argIndex = 1;
            if (args.Length >= 2)
            {
                if (File.Exists(args[1]))
                {
                    if (Path.GetExtension(args[1]).Equals(".csv", StringComparison.OrdinalIgnoreCase)) { csvArg = args[1]; argIndex = 2; }
                    else { Console.Error.WriteLine("Invalid CSV argument: '{0}' (file exists but is not a .csv)", args[1]); Environment.Exit(1); }
                }
                else { Console.Error.WriteLine("Invalid CSV argument: '{0}' (file not found)", args[1]); Environment.Exit(1); }
            }
            if (args.Length > argIndex) { double.TryParse(args[argIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out insThr); argIndex++; }
            if (args.Length > argIndex) { int.TryParse(args[argIndex], out chgThr); argIndex++; }

            rawFile.SelectInstrument(Device.MS, 1);
            int fMS = rawFile.RunHeaderEx.FirstSpectrum, nMS = rawFile.RunHeaderEx.LastSpectrum;

            int scanCount = nMS - fMS + 1;
            // allowable tolerance (minutes) when comparing scan time to target Start/End
            double timeTolerance = 0.01;
            try { Console.WriteLine("#filename:\t{0}\n#prescan(s):\t{1}\n#RT length:\t{2}", rawFileName, scanCount, rawFile.RunHeaderEx.EndTime - rawFile.RunHeaderEx.StartTime); } catch { }

            Console.WriteLine("scan\tBasePeakMass\tTIC\tmaxIntSum\ttitle\tmaxMass\ttime\tmaxInt\tcharge");
            var ciFileName = rawFileName + ".cI.tsv";

            string csvName = "targeted peptides inkl mito sched.csv"; string csvPath = null;
            if (!string.IsNullOrEmpty(csvArg) && File.Exists(csvArg)) csvPath = csvArg;
            else if (File.Exists(csvName)) csvPath = csvName;
            else { try { var candidate = Path.Combine(Path.GetDirectoryName(rawFileName), csvName); if (File.Exists(candidate)) csvPath = candidate; } catch { csvPath = null; } }

            var targets = new List<Target>(); string[] csvHeaderFields = null;
            if (csvPath != null)
            {
                try
                {
                    using (var sr = new StreamReader(csvPath))
                    {
                        var headerLine = sr.ReadLine();
                        if (headerLine != null)
                        {
                            csvHeaderFields = headerLine.Split(',');
                            int massIdx = Array.FindIndex(csvHeaderFields, h => h.Trim().Equals("Mass [m/z]", StringComparison.OrdinalIgnoreCase));
                            int startIdx = Array.FindIndex(csvHeaderFields, h => h.Trim().Equals("Start [min]", StringComparison.OrdinalIgnoreCase));
                            int endIdx = Array.FindIndex(csvHeaderFields, h => h.Trim().Equals("End [min]", StringComparison.OrdinalIgnoreCase));
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var fields = line.Split(','); double mass = double.NaN; double smin = double.NaN; double emin = double.NaN;
                                if (massIdx >= 0 && massIdx < fields.Length) double.TryParse(fields[massIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out mass);
                                if (startIdx >= 0 && startIdx < fields.Length) double.TryParse(fields[startIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out smin);
                                if (endIdx >= 0 && endIdx < fields.Length) double.TryParse(fields[endIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out emin);
                                var targ = new Target(fields, mass);
                                targ.StartMin = smin; targ.EndMin = emin;
                                targets.Add(targ);
                            }
                        }
                    }
                    Console.WriteLine("Read {0} targets from: {1}", targets.Count, csvPath);
                }
                catch (Exception ex) { Console.Error.WriteLine("Warning: failed to read target CSV '{0}': {1}", csvPath, ex.Message); }
            }
            else Console.WriteLine("Target CSV not found; skipping targeted TIC accumulation.");

            bool skipScanLoop = false;
            if (File.Exists(ciFileName))
            {
                try
                {
                    var lines = File.ReadAllLines(ciFileName);
                    int prescanVal = -1;
                    foreach (var ln in lines)
                    {
                        if (ln == null) continue; var tln = ln.TrimStart();
                        if (tln.StartsWith("#prescan(s):", StringComparison.OrdinalIgnoreCase)) { var parts = tln.Split(new[] { '\t', ':' }, 3, StringSplitOptions.RemoveEmptyEntries); if (parts.Length >= 2) { var token = parts[parts.Length - 1].Trim(); int.TryParse(token, out prescanVal); } break; }
                    }
                    int dataRows = lines.Count(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && !l.StartsWith("scan\t"));
                    if (prescanVal >= 0 && prescanVal == dataRows) { skipScanLoop = true; Console.WriteLine("Found existing complete cI.tsv ({0}) with prescan(s)={1}; skipping scan extraction.", ciFileName, prescanVal); }
                    else if (prescanVal < 0 && dataRows == scanCount) { skipScanLoop = true; Console.WriteLine("Found existing cI.tsv ({0}) with {1} data rows matching expected {2} scans; skipping scan extraction.", ciFileName, dataRows, scanCount); }
                }
                catch (Exception ex) { Console.Error.WriteLine("Warning: failed to validate existing cI.tsv '{0}': {1}", ciFileName, ex.Message); }
            }

            var ciRowByScan = new Dictionary<string, string>();
            StreamWriter ciWriter = null; if (!skipScanLoop) { ciWriter = new StreamWriter(ciFileName); ciWriter.WriteLine("scan\tBasePeakMass\tTIC\tmaxIntSum\ttitle\tmaxMass\ttime\tmaxInt\tcharge"); }

            int tms = 0; double tic = 0; double maxIntSum = 0; int lastPercent = -1;

            if (!skipScanLoop)
            {
                for (int i = fMS; i <= nMS; i++)
                {
                    double time = rawFile.RetentionTimeFromScanNumber(i);
                    string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(i));
                    var scanStatistics = rawFile.GetScanStatsForScanNumber(i);
                    double maxMass = scanStatistics.BasePeakMass, maxInt = scanStatistics.BasePeakIntensity; string charge = "";
                    var logEntry = rawFile.GetTrailerExtraInformation(i);
                    for (var l = 0; l < logEntry.Length; l++) if (logEntry.Labels[l] == "Charge State:") charge = logEntry.Values[l];
                    maxIntSum += maxInt;
                    // parse precursor/target mass from title when available (e.g. "... 1120.0691@hcd27.00 ...")
                    double obsFromTitle = double.NaN;
                    try
                    {
                        var m = Regex.Match(title, "(\\d+\\.\\d+)(?=@)");
                        if (m.Success) double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out obsFromTitle);
                        else
                        {
                            m = Regex.Match(title, "(\\d+\\.\\d+)");
                            if (m.Success) double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out obsFromTitle);
                        }
                    }
                    catch { }

                    if (targets.Count > 0)
                    {
                        // Only use the mass parsed from the title for matching. Do not fall back to BasePeakMass.
                        double obs = obsFromTitle;
                        if (!double.IsNaN(obs))
                        {
                            double ticThis = scanStatistics.TIC;
                            for (int ti = 0; ti < targets.Count; ti++)
                            {
                                var targ = targets[ti]; double targetMass = targ.Mass; if (double.IsNaN(targetMass)) continue; double tol = 0.0001;
                                if (Math.Abs(obs - targetMass) <= tol)
                                {
                                    // enforce time window if provided
                                    bool inWindow = true;
                                    if (!double.IsNaN(targ.StartMin) || !double.IsNaN(targ.EndMin))
                                    {
                                            if (!double.IsNaN(targ.StartMin) && time < targ.StartMin - timeTolerance) inWindow = false;
                                            if (!double.IsNaN(targ.EndMin) && time > targ.EndMin + timeTolerance) inWindow = false;
                                    }
                                    if (inWindow)
                                    {
                                        targ.AccTIC += ticThis;
                                        targ.MatchedCount += 1;
                                        try { targ.MatchedMasses.Add(i.ToString(CultureInfo.InvariantCulture)); } catch { targ.MatchedMasses.Add(i.ToString()); }
                                        try { targ.AccTime += time; targ.MatchedTimes.Add(time); } catch { }
                                    }
                                }
                            }
                        }
                    }
                    tic += scanStatistics.TIC; int parsedCharge = 0; int.TryParse(charge, out parsedCharge); if (maxInt >= insThr && (chgThr == 0 || parsedCharge >= chgThr)) tms++;
                    var line = string.Format("{0}\t{8}\t{7}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}", i, maxIntSum, title, maxMass, time, maxInt, charge, scanStatistics.TIC, scanStatistics.BasePeakMass);
                    if (ciWriter != null)
                    {
                        ciWriter.WriteLine(line);
                        try { ciRowByScan[i.ToString(CultureInfo.InvariantCulture)] = line; } catch { ciRowByScan[i.ToString()] = line; }
                    }
                    int processed = i - fMS + 1; int percent = (int)((processed * 100L) / scanCount); if (percent != lastPercent) { lastPercent = percent; int barWidth = 10; int filled = (percent * barWidth) / 100; string bar = new string('#', filled) + new string('-', barWidth - filled); Console.Write(string.Format("\rProgress: [{0}] {1,3}%", bar, percent)); }
                }
                Console.WriteLine(); Console.WriteLine("#TIC>={0}intensity:\t{1:N2}", insThr, tic); Console.WriteLine("#Ions>=charge{0}:\t{1}", chgThr, tms); rawFile.Dispose(); if (ciWriter != null) { ciWriter.Flush(); ciWriter.Close(); }
            }
            else
            {
                try
                {
                    using (var sr = new StreamReader(ciFileName))
                    {
                        string header = null; while (!sr.EndOfStream) { var ln = sr.ReadLine(); if (ln == null) break; if (ln.StartsWith("#") || ln.Trim().Length == 0) continue; header = ln; break; }
                        int ticIdx = -1, timeIdx = -1, titleIdx = -1;
                        if (header != null)
                        {
                            var cols = header.Split('\t');
                            for (int c = 0; c < cols.Length; c++)
                            {
                                var h = cols[c].Trim();
                                if (h.Equals("TIC", StringComparison.OrdinalIgnoreCase)) ticIdx = c;
                                if (h.Equals("time", StringComparison.OrdinalIgnoreCase) || h.Equals("rt", StringComparison.OrdinalIgnoreCase)) timeIdx = c;
                                if (h.Equals("title", StringComparison.OrdinalIgnoreCase)) titleIdx = c;
                            }
                        }
                        while (!sr.EndOfStream)
                        {
                            var ln = sr.ReadLine(); if (ln == null) break; if (ln.StartsWith("#") || ln.Trim().Length == 0) continue;
                            var parts = ln.Split('\t'); double m = double.NaN; double t = 0; double tt = double.NaN;
                            // Extract observed mass only from the title column; do not use BasePeakMass for matching.
                            if (titleIdx >= 0 && titleIdx < parts.Length)
                            {
                                try
                                {
                                    var titleField = parts[titleIdx];
                                    var mm = Regex.Match(titleField, "(\\d+\\.\\d+)(?=@)");
                                    if (mm.Success) double.TryParse(mm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out m);
                                    else { mm = Regex.Match(titleField, "(\\d+\\.\\d+)"); if (mm.Success) double.TryParse(mm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out m); }
                                }
                                catch { }
                            }
                            if (ticIdx >= 0 && ticIdx < parts.Length) double.TryParse(parts[ticIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out t);
                            if (timeIdx >= 0 && timeIdx < parts.Length) double.TryParse(parts[timeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out tt);
                            if (targets.Count > 0 && !double.IsNaN(m))
                            {
                                for (int ti = 0; ti < targets.Count; ti++)
                                {
                                    var titem = targets[ti]; double targetMass = titem.Mass; if (double.IsNaN(targetMass)) continue; double tol = 0.0001;
                                    if (Math.Abs(m - targetMass) <= tol)
                                    {
                                        // enforce time window if provided
                                        bool inWindow2 = true;
                                        if (!double.IsNaN(titem.StartMin) || !double.IsNaN(titem.EndMin))
                                        {
                                            if (!double.IsNaN(titem.StartMin) && tt < titem.StartMin - timeTolerance) inWindow2 = false;
                                            if (!double.IsNaN(titem.EndMin) && tt > titem.EndMin + timeTolerance) inWindow2 = false;
                                        }
                                        if (inWindow2)
                                        {
                                            titem.AccTIC += t;
                                            titem.MatchedCount += 1;
                                            // store the scan id from the cI.tsv (first column) when available
                                            try { if (parts.Length > 0) titem.MatchedMasses.Add(parts[0]); else titem.MatchedMasses.Add(m.ToString(CultureInfo.InvariantCulture)); } catch { try { titem.MatchedMasses.Add(parts[0]); } catch { titem.MatchedMasses.Add(m.ToString()); } }
                                            if (!double.IsNaN(tt)) { titem.AccTime += tt; titem.MatchedTimes.Add(tt); }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("Warning: failed to read existing cI.tsv for accumulation: {0}", ex.Message); }
                rawFile.Dispose();
            }

            if (targets.Count > 0)
            {
                string csvBase = "targets.csv"; try { if (!string.IsNullOrEmpty(csvPath)) csvBase = Path.GetFileName(csvPath); else csvBase = Path.GetFileName(csvName); } catch { csvBase = Path.GetFileName(csvName); }
                var accFile = rawFileName + "." + csvBase;
                    try
                    {
                        Func<string, string> esc = (s) => { if (s == null) return ""; if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r')) return "\"" + s.Replace("\"", "\"\"") + "\""; return s; };
                        using (var outw = new StreamWriter(accFile))
                        {
                            if (csvHeaderFields != null) outw.Write(string.Join(",", csvHeaderFields.Select(h => esc(h)))); else outw.Write("Compound,Mass [m/z]");
                            outw.Write(",AccumulatedTIC,MatchedCount,MatchedMasses,MatchedTimes\n");
                            foreach (var t in targets)
                            {
                                var fields = t.Fields; if (fields != null && fields.Length > 0) outw.Write(string.Join(",", fields.Select(f => esc(f)))); else outw.Write(esc("")); outw.Write(",");
                                var matchedMasses = t.MatchedMasses != null && t.MatchedMasses.Count > 0 ? string.Join(";", t.MatchedMasses) : "";
                                var matchedTimes = t.MatchedTimes != null && t.MatchedTimes.Count > 0 ? string.Join(";", t.MatchedTimes.Select(d => d.ToString(CultureInfo.InvariantCulture))) : "";
                                outw.Write(string.Format(CultureInfo.InvariantCulture, "{0:0.######},{1},{2},{3}\n", t.AccTIC, t.MatchedCount, esc(matchedMasses), esc(matchedTimes)));
                            }
                        }
                        Console.WriteLine("Wrote targeted accumulation: {0}", accFile);

                        // After writing accumulation, produce analysis reports: duplicated scans and unmatched scans
                        try
                        {
                            var scanToTargets = new Dictionary<string, List<int>>();
                            for (int ti = 0; ti < targets.Count; ti++)
                            {
                                var t = targets[ti];
                                if (t.MatchedMasses == null) continue;
                                foreach (var scanId in t.MatchedMasses)
                                {
                                    if (string.IsNullOrWhiteSpace(scanId)) continue;
                                    if (!scanToTargets.ContainsKey(scanId)) scanToTargets[scanId] = new List<int>();
                                    scanToTargets[scanId].Add(ti);
                                }
                            }

                            // read all scans from cI.tsv to detect unmatched
                            var scansInCi = new List<string>();
                            ciRowByScan.Clear();
                            try
                            {
                                using (var sr2 = new StreamReader(ciFileName))
                                {
                                    while (!sr2.EndOfStream)
                                    {
                                        var ln2 = sr2.ReadLine(); if (ln2 == null) break; if (ln2.StartsWith("#") || ln2.Trim().Length == 0) continue;
                                        if (ln2.TrimStart().ToLower().StartsWith("scan\t")) continue; // header
                                        var p = ln2.Split('\t'); if (p.Length > 0) { scansInCi.Add(p[0]); if (!ciRowByScan.ContainsKey(p[0])) ciRowByScan[p[0]] = ln2; }
                                    }
                                }
                            }
                            catch { }

                            int totalMatchedOccurrences = scanToTargets.Values.Sum(l => l.Count);
                            int uniqueMatchedScans = scanToTargets.Keys.Count;
                            var duplicates = scanToTargets.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
                            var unmatched = scansInCi.Where(s => !scanToTargets.ContainsKey(s)).ToList();

                            // write duplicated_scans.tsv (include full target CSV rows for each matched target)
                            var dupFile = Path.Combine(Path.GetDirectoryName(accFile) ?? ".", Path.GetFileNameWithoutExtension(accFile) + ".duplicated_scans.tsv");
                            using (var dw = new StreamWriter(dupFile))
                            {
                                dw.WriteLine("scan\tmatch_count\tci_row\tmatched_target_rows");
                                foreach (var kv in duplicates.OrderBy(k => {
                                    int v; return int.TryParse(k.Key, out v) ? v : int.MaxValue;
                                }))
                                {
                                    // build list of full CSV rows for each matched target index
                                    var rows = new List<string>();
                                    foreach (var ti in kv.Value)
                                    {
                                        try
                                        {
                                            var f = targets[ti].Fields;
                                            if (f != null && f.Length > 0) rows.Add(string.Join(",", f)); else rows.Add("");
                                        }
                                        catch { rows.Add(""); }
                                    }
                                    string ciRow = ciRowByScan.ContainsKey(kv.Key) ? ciRowByScan[kv.Key] : "";
                                    dw.WriteLine(string.Format("{0}\t{1}\t{2}\t{3}", kv.Key, kv.Value.Count, ciRow, string.Join(" || ", rows)));
                                }
                            }

                            // write unmatched_scans.tsv (include full cI.tsv row for each unmatched scan)
                            var unFile = Path.Combine(Path.GetDirectoryName(accFile) ?? ".", Path.GetFileNameWithoutExtension(accFile) + ".unmatched_scans.tsv");
                            using (var uw = new StreamWriter(unFile))
                            {
                                foreach (var s in unmatched)
                                {
                                    if (ciRowByScan.ContainsKey(s)) uw.WriteLine(ciRowByScan[s]); else uw.WriteLine(s);
                                }
                            }

                            // write summary to console (do not write summary file)
                            try
                            {
                                Console.WriteLine("--- Match Summary ---");
                                Console.WriteLine("cI TSV: {0}", ciFileName);
                                Console.WriteLine("accum CSV: {0}", accFile);
                                Console.WriteLine("Total scans in cI.tsv: {0}", scansInCi.Count);
                                Console.WriteLine("Total matched occurrences (sum of matched counts across targets): {0}", totalMatchedOccurrences);
                                Console.WriteLine("Unique matched scans: {0}", uniqueMatchedScans);
                                Console.WriteLine("Scans matched to >1 target: {0}", duplicates.Count);
                                Console.WriteLine("Scans with no match: {0}", unmatched.Count);
                                Console.WriteLine();
                                Console.WriteLine("Generated files:");
                                Console.WriteLine(" - {0}", dupFile);
                                Console.WriteLine(" - {0}", unFile);
                                Console.WriteLine("---------------------");
                            }
                            catch (Exception) { }

                            Console.WriteLine("Wrote match reports: {0}, {1}", dupFile, unFile);
                        }
                        catch (Exception ex) { Console.Error.WriteLine("Warning: failed to write match reports: {0}", ex.Message); }
                    }
                    catch (Exception ex) { Console.Error.WriteLine("Warning: failed to write targeted accumulation file: {0}", ex.Message); }
            }

            if (!skipScanLoop) Console.WriteLine("Wrote: {0}", ciFileName); else Console.WriteLine("Used existing: {0}", ciFileName);
        }
    }
}
