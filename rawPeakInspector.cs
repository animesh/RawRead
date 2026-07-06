// compile: mcs rawPeakInspector.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll /reference:ThermoFisher.CommonCore.Data.dll -out:rawPeakInspector.exe
// run example: mono rawPeakInspector.exe 260629_Solveig_3_IgG.raw 22.5 29.5 10000000 500 10 12 30 0 35 22572.988641,22573.970103,22576.980046 IgG_anchor_check
// args: rawFile minRt maxRt minPeakIntensity maxPeaksPerScan ppmTolerance minCharge maxCharge minIsotope maxIsotope candidateMassCsv outputPrefix

using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using ThermoFisher.CommonCore.RawFileReader;
using ThermoFisher.CommonCore.Data.Business;

namespace RawPeakInspector
{
    internal class Program
    {
        private const double Proton = 1.007276466812;
        private const double C13MinusC12 = 1.00335483507;

        private class Peak
        {
            public int Index;
            public double Mz;
            public double Intensity;
            public int Charge;
        }

        private class MatchRow
        {
            public int Scan;
            public double Rt;
            public double CandidateMass;
            public int IsotopeIndex;
            public int Charge;
            public double ExpectedMz;
            public double MatchedMz;
            public double DeltaPpm;
            public double Intensity;
            public int PeakCharge;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("USAGE:");
                Console.WriteLine("{0} rawFile [minRt=22.5] [maxRt=29.5] [minPeakIntensity=10000000] [maxPeaksPerScan=500] [ppmTolerance=10] [minCharge=12] [maxCharge=30] [minIsotope=0] [maxIsotope=35] [candidateMassCsv=22572.988641,22573.970103,22576.980046] [outputPrefix=raw_peak_inspection]", AppDomain.CurrentDomain.FriendlyName);
                return;
            }

            string rawPath = args[0];
            double minRt = ParseD(args, 1, 22.5);
            double maxRt = ParseD(args, 2, 29.5);
            double minPeakIntensity = ParseD(args, 3, 10000000.0);
            int maxPeaksPerScan = ParseI(args, 4, 500);
            double ppmTolerance = ParseD(args, 5, 10.0);
            int minCharge = ParseI(args, 6, 12);
            int maxCharge = ParseI(args, 7, 30);
            int minIsotope = ParseI(args, 8, 0);
            int maxIsotope = ParseI(args, 9, 35);
            string candidateMassCsv = args.Length > 10 ? args[10] : "22572.988641,22573.970103,22576.980046";
            string outputPrefix = args.Length > 11 ? args[11] : "raw_peak_inspection";

            if (maxCharge < minCharge) maxCharge = minCharge;
            if (maxIsotope < minIsotope) maxIsotope = minIsotope;
            if (maxPeaksPerScan < 0) maxPeaksPerScan = 0;

            List<double> candidateMasses = ParseMassCsv(candidateMassCsv);
            if (candidateMasses.Count == 0)
            {
                Console.Error.WriteLine("No valid candidate masses found in candidateMassCsv: {0}", candidateMassCsv);
                return;
            }

            Console.WriteLine("#rawFile:\t{0}", rawPath);
            Console.WriteLine("#minRt:\t{0}", minRt.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxRt:\t{0}", maxRt.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minPeakIntensity:\t{0}", minPeakIntensity.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxPeaksPerScan:\t{0}", maxPeaksPerScan.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#ppmTolerance:\t{0}", ppmTolerance.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minCharge:\t{0}", minCharge.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxCharge:\t{0}", maxCharge.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#minIsotope:\t{0}", minIsotope.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#maxIsotope:\t{0}", maxIsotope.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("#candidateMassCsv:\t{0}", candidateMassCsv);
            Console.WriteLine("#outputPrefix:\t{0}", outputPrefix);

            var raw = RawFileReaderAdapter.FileFactory(rawPath);
            if (!raw.IsOpen || raw.IsError)
            {
                Console.Error.WriteLine("Error opening RAW file: {0} FileError: {1}", rawPath, raw.FileError);
                return;
            }

            raw.SelectInstrument(Device.MS, 1);

            string peakFile = outputPrefix + ".high_intensity_peaks.tsv";
            string matchFile = outputPrefix + ".isotope_peak_matches.tsv";
            string summaryFile = outputPrefix + ".isotope_peak_summary.tsv";
            string apexFile = outputPrefix + ".apex_scan_windows.tsv";

            List<MatchRow> allMatches = new List<MatchRow>();
            List<Tuple<int, double, double>> scanTic = new List<Tuple<int, double, double>>();

            using (var peakWriter = new StreamWriter(peakFile))
            using (var matchWriter = new StreamWriter(matchFile))
            using (var apexWriter = new StreamWriter(apexFile))
            {
                peakWriter.WriteLine("Scan\tRT\tRank\tMz\tIntensity\tCentroidCharge");
                matchWriter.WriteLine("Scan\tRT\tCandidateMass\tIsotopeIndex\tCharge\tExpectedMz\tMatchedMz\tDeltaPpm\tIntensity\tCentroidCharge");
                apexWriter.WriteLine("Scan\tRT\tCandidateMass\tCharge\tIsotopeIndex\tExpectedMz\tWindowMinMz\tWindowMaxMz\tMatchedMz\tDeltaPpm\tIntensity\tCentroidCharge");

                int firstScan = raw.RunHeaderEx.FirstSpectrum;
                int lastScan = raw.RunHeaderEx.LastSpectrum;

                for (int scan = firstScan; scan <= lastScan; scan++)
                {
                    double rt;
                    try { rt = raw.RetentionTimeFromScanNumber(scan); }
                    catch { continue; }

                    if (rt < minRt || rt > maxRt) continue;
                    if (!LooksLikeMs1(raw, scan)) continue;

                    CentroidStream cs = null;
                    try { cs = raw.GetCentroidStream(scan, false); }
                    catch { cs = null; }

                    if (cs == null || cs.Length <= 0) continue;
                    if (cs.Masses == null || cs.Intensities == null || cs.Masses.Length != cs.Intensities.Length) continue;

                    double[] mz = cs.Masses;
                    double[] inten = cs.Intensities;
                    double[] chargeArray = null;
                    try { chargeArray = cs.Charges; } catch { chargeArray = null; }

                    List<Peak> peaks = new List<Peak>();
                    for (int i = 0; i < mz.Length; i++)
                    {
                        if (inten[i] >= minPeakIntensity)
                        {
                            peaks.Add(new Peak { Index = i, Mz = mz[i], Intensity = inten[i], Charge = GetPeakCharge(chargeArray, i) });
                        }
                    }

                    peaks = peaks.OrderByDescending(p => p.Intensity).ToList();
                    if (maxPeaksPerScan > 0 && peaks.Count > maxPeaksPerScan) peaks = peaks.Take(maxPeaksPerScan).ToList();

                    double scanTotalIntensity = peaks.Sum(p => p.Intensity);
                    scanTic.Add(new Tuple<int, double, double>(scan, rt, scanTotalIntensity));

                    int rank = 1;
                    foreach (Peak p in peaks)
                    {
                        peakWriter.WriteLine(
                            scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                            rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                            rank.ToString(CultureInfo.InvariantCulture) + "\t" +
                            p.Mz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                            p.Intensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                            p.Charge.ToString(CultureInfo.InvariantCulture)
                        );
                        rank++;
                    }

                    foreach (double candidateMass in candidateMasses)
                    {
                        for (int z = minCharge; z <= maxCharge; z++)
                        {
                            for (int iso = minIsotope; iso <= maxIsotope; iso++)
                            {
                                double expectedMz = (candidateMass + iso * C13MinusC12 + z * Proton) / z;
                                double tol = expectedMz * ppmTolerance / 1e6;
                                Peak matched = FindHighestPeak(mz, inten, chargeArray, expectedMz - tol, expectedMz + tol);

                                if (matched != null)
                                {
                                    double deltaPpm = (matched.Mz - expectedMz) / expectedMz * 1e6;
                                    MatchRow row = new MatchRow
                                    {
                                        Scan = scan,
                                        Rt = rt,
                                        CandidateMass = candidateMass,
                                        IsotopeIndex = iso,
                                        Charge = z,
                                        ExpectedMz = expectedMz,
                                        MatchedMz = matched.Mz,
                                        DeltaPpm = deltaPpm,
                                        Intensity = matched.Intensity,
                                        PeakCharge = matched.Charge
                                    };
                                    allMatches.Add(row);

                                    matchWriter.WriteLine(
                                        scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                        candidateMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                        iso.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        z.ToString(CultureInfo.InvariantCulture) + "\t" +
                                        expectedMz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                        matched.Mz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                        deltaPpm.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                                        matched.Intensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                                        matched.Charge.ToString(CultureInfo.InvariantCulture)
                                    );
                                }
                            }
                        }
                    }
                }

                if (scanTic.Count > 0)
                {
                    Tuple<int, double, double> apexScan = scanTic.OrderByDescending(x => x.Item3).First();
                    int scan = apexScan.Item1;
                    double rt = apexScan.Item2;
                    CentroidStream cs = raw.GetCentroidStream(scan, false);
                    double[] mz = cs.Masses;
                    double[] inten = cs.Intensities;
                    double[] chargeArray = null;
                    try { chargeArray = cs.Charges; } catch { chargeArray = null; }

                    foreach (double candidateMass in candidateMasses)
                    {
                        for (int z = minCharge; z <= maxCharge; z++)
                        {
                            for (int iso = minIsotope; iso <= maxIsotope; iso++)
                            {
                                double expectedMz = (candidateMass + iso * C13MinusC12 + z * Proton) / z;
                                double tol = expectedMz * ppmTolerance / 1e6;
                                Peak matched = FindHighestPeak(mz, inten, chargeArray, expectedMz - tol, expectedMz + tol);
                                string matchedMz = "";
                                string deltaPpm = "";
                                string intensity = "";
                                string peakCharge = "";

                                if (matched != null)
                                {
                                    matchedMz = matched.Mz.ToString("F6", CultureInfo.InvariantCulture);
                                    deltaPpm = ((matched.Mz - expectedMz) / expectedMz * 1e6).ToString("F4", CultureInfo.InvariantCulture);
                                    intensity = matched.Intensity.ToString("G17", CultureInfo.InvariantCulture);
                                    peakCharge = matched.Charge.ToString(CultureInfo.InvariantCulture);
                                }

                                apexWriter.WriteLine(
                                    scan.ToString(CultureInfo.InvariantCulture) + "\t" +
                                    rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                    candidateMass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                    z.ToString(CultureInfo.InvariantCulture) + "\t" +
                                    iso.ToString(CultureInfo.InvariantCulture) + "\t" +
                                    expectedMz.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                    (expectedMz - tol).ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                    (expectedMz + tol).ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                    matchedMz + "\t" +
                                    deltaPpm + "\t" +
                                    intensity + "\t" +
                                    peakCharge
                                );
                            }
                        }
                    }
                }
            }

            WriteSummary(summaryFile, allMatches, candidateMasses, minCharge, maxCharge, minIsotope, maxIsotope);
            raw.Dispose();

            Console.WriteLine("Wrote high intensity peaks: {0}", peakFile);
            Console.WriteLine("Wrote isotope peak matches: {0}", matchFile);
            Console.WriteLine("Wrote isotope peak summary: {0}", summaryFile);
            Console.WriteLine("Wrote apex scan isotope windows: {0}", apexFile);
        }

        private static void WriteSummary(string path, List<MatchRow> matches, List<double> candidateMasses, int minCharge, int maxCharge, int minIsotope, int maxIsotope)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("CandidateMass\tCharge\tIsotopeIndex\tMatchedScans\tRtMin\tRtMax\tApexRt\tSumIntensity\tMaxIntensity\tMedianDeltaPpm\tMeanMatchedMz");

                foreach (double mass in candidateMasses)
                {
                    for (int z = minCharge; z <= maxCharge; z++)
                    {
                        for (int iso = minIsotope; iso <= maxIsotope; iso++)
                        {
                            List<MatchRow> rows = matches.Where(r => Math.Abs(r.CandidateMass - mass) < 0.000001 && r.Charge == z && r.IsotopeIndex == iso).ToList();
                            if (rows.Count == 0) continue;

                            MatchRow apex = rows.OrderByDescending(r => r.Intensity).First();
                            double sumIntensity = rows.Sum(r => r.Intensity);
                            double maxIntensity = rows.Max(r => r.Intensity);
                            double medianDelta = Median(rows.Select(r => r.DeltaPpm).ToList());
                            double meanMz = rows.Average(r => r.MatchedMz);

                            writer.WriteLine(
                                mass.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                z.ToString(CultureInfo.InvariantCulture) + "\t" +
                                iso.ToString(CultureInfo.InvariantCulture) + "\t" +
                                rows.Select(r => r.Scan).Distinct().Count().ToString(CultureInfo.InvariantCulture) + "\t" +
                                rows.Min(r => r.Rt).ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                rows.Max(r => r.Rt).ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                apex.Rt.ToString("F6", CultureInfo.InvariantCulture) + "\t" +
                                sumIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                                maxIntensity.ToString("G17", CultureInfo.InvariantCulture) + "\t" +
                                medianDelta.ToString("F4", CultureInfo.InvariantCulture) + "\t" +
                                meanMz.ToString("F6", CultureInfo.InvariantCulture)
                            );
                        }
                    }
                }
            }
        }

        private static bool LooksLikeMs1(dynamic raw, int scan)
        {
            string text = "";
            try { text = string.Join(" ", raw.GetScanEventForScanNumber(scan)); } catch { text = ""; }
            if (text.Length == 0) return true;
            return text.IndexOf("ms", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("@", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static int GetPeakCharge(double[] chargeArray, int index)
        {
            try
            {
                if (chargeArray != null && index < chargeArray.Length) return (int)Math.Round(chargeArray[index]);
            }
            catch { }
            return 0;
        }

        private static Peak FindHighestPeak(double[] mz, double[] intensity, double[] chargeArray, double lo, double hi)
        {
            int start = LowerBound(mz, lo);
            Peak best = null;
            for (int i = start; i < mz.Length; i++)
            {
                if (mz[i] > hi) break;
                if (mz[i] < lo) continue;
                if (best == null || intensity[i] > best.Intensity)
                {
                    best = new Peak { Index = i, Mz = mz[i], Intensity = intensity[i], Charge = GetPeakCharge(chargeArray, i) };
                }
            }
            return best;
        }

        private static int LowerBound(double[] a, double value)
        {
            int left = 0;
            int right = a.Length;
            while (left < right)
            {
                int mid = left + (right - left) / 2;
                if (a[mid] < value) left = mid + 1;
                else right = mid;
            }
            return left;
        }

        private static List<double> ParseMassCsv(string csv)
        {
            List<double> masses = new List<double>();
            string[] parts = csv.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                double v;
                if (double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) masses.Add(v);
            }
            return masses;
        }

        private static int ParseI(string[] args, int index, int fallback)
        {
            int v;
            if (args.Length > index && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static double ParseD(string[] args, int index, double fallback)
        {
            double v;
            if (args.Length > index && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static double Median(List<double> values)
        {
            List<double> clean = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
            if (clean.Count == 0) return double.NaN;
            int mid = clean.Count / 2;
            if (clean.Count % 2 == 1) return clean[mid];
            return 0.5 * (clean[mid - 1] + clean[mid]);
        }
    }
}
