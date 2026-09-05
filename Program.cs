using System;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using ThermoFisher.CommonCore.Data.Business;
using ThermoFisher.CommonCore.RawFileReader;

Console.WriteLine("RawRead / Thermo RawFileReader browser-WASM probe");
Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
Console.WriteLine("Thermo assembly: " + typeof(RawFileReaderAdapter).Assembly.FullName);

public partial class RawProbe
{
    [JSExport]
    public static string OpenRaw(byte[] rawBytes, string fileName)
    {
        string safeName = Path.GetFileName(fileName);
        string rawPath = "/raw/" + safeName;

        try
        {
            Directory.CreateDirectory("/raw");
            File.WriteAllBytes(rawPath, rawBytes);

            Console.WriteLine($"Received {rawBytes.Length:N0} bytes as {safeName}");
            Console.WriteLine($"File.Exists: {File.Exists(rawPath)}");
            Console.WriteLine($"File.Length: {new FileInfo(rawPath).Length:N0}");
            Console.WriteLine("Calling RawFileReaderAdapter.FileFactory...");

            var rawFile = RawFileReaderAdapter.FileFactory(rawPath);

            try
            {
                Console.WriteLine($"IsOpen : {rawFile.IsOpen}");
                Console.WriteLine($"IsError: {rawFile.IsError}");

                if (!rawFile.IsOpen || rawFile.IsError)
                {
                    return $"OPEN FAILED\n{rawFile.FileError}";
                }

                rawFile.SelectInstrument(Device.MS, 1);

                var header = rawFile.RunHeaderEx;
                int first = header.FirstSpectrum;
                int last = header.LastSpectrum;

                Console.WriteLine($"First spectrum: {first}");
                Console.WriteLine($"Last spectrum : {last}");
                Console.WriteLine($"Start time    : {header.StartTime}");
                Console.WriteLine($"End time      : {header.EndTime}");

                if (last < first)
                    return "OPEN OK, but invalid scan range";

                var stats = rawFile.GetScanStatsForScanNumber(first);
                var seg = rawFile.GetSegmentedScanFromScanNumber(first, stats);
                var centroid = rawFile.GetCentroidStream(first, false);

                return $"OPEN OK\nfirst={first}\nlast={last}\n" +
                       $"centroid={stats.IsCentroidScan}\n" +
                       $"segmented points={seg.Positions.Length}\n" +
                       $"centroid points={centroid.Length}";
            }
            finally
            {
                rawFile.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return "EXCEPTION\n" + ex;
        }
    }
}
