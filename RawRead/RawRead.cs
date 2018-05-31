namespace RawRead{
    using System;
    using ThermoFisher.CommonCore.Data.Business;
    using ThermoFisher.CommonCore.RawFileReader;  /* RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap */
    class RawRead2PeakList{
        static void Main(string[] args){
            if (args.Length != 3) { Console.WriteLine("USAGE: {0} fileName intensityThreshold chargeThreshold", AppDomain.CurrentDomain.FriendlyName); return; }
            var rawFile = RawFileReaderAdapter.FileFactory(args[0]);
            rawFile.SelectInstrument(Device.MS, 1);
            int fms = rawFile.RunHeaderEx.FirstSpectrum;
            int nms = rawFile.RunHeaderEx.LastSpectrum;
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName);
            Console.WriteLine("#prescan(s):\t" + nms);
            Console.WriteLine("#RT length:\t" + (eTime-sTime));
            Console.WriteLine("#version:\t" + rawFile.FileHeader.Revision);
            Console.WriteLine("#create date:\t" + rawFile.FileHeader.CreationDate);
            Console.WriteLine("#machine:\t" + rawFile.FileHeader.WhoCreatedId);
            Console.WriteLine("#serial#:\t" + rawFile.GetInstrumentData().SerialNumber);
            Console.WriteLine("#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion);
            Console.WriteLine("#resolution:\t{0:F3}", rawFile.RunHeaderEx.MassResolution);
            Console.WriteLine("#massrange:\t{0:F4}-{1:F4}", rawFile.RunHeaderEx.LowMass, rawFile.RunHeaderEx.HighMass);
            int tms = 0;
            double tic = 0;
            double maxIntSum = 0;
            for (int i = fms; i < nms; i++){
                var centroidStream = rawFile.GetCentroidStream(i, false);
                double time = rawFile.RetentionTimeFromScanNumber(i);
                var title = rawFile.GetScanEventForScanNumber(i);
                double maxMass = 0;
                double maxInt = 0;
                for (int j = 0; j < centroidStream.Length; j++){
                    //Console.WriteLine("\t\t\t{0}\t{1:F4}\t{2:F0}\t{3:F0}", j, centroidStream.Masses[j], centroidStream.Intensities[j], centroidStream.Charges[j]);
                    if (centroidStream.Charges[j] >= double.Parse(args[2]) && centroidStream.Intensities[j] >= double.Parse(args[1])) { tic += centroidStream.Intensities[j]; tms++; }
                    if (centroidStream.Intensities[j] >= maxInt) { maxInt = centroidStream.Intensities[j]; maxMass = centroidStream.Masses[j]; maxIntSum += maxInt; }
                }
                Console.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t", i, maxIntSum, string.Join(Environment.NewLine, title), centroidStream.Length, maxMass, time, maxInt);
            }
            Console.WriteLine("#TIC>={0}:\t{1}", args[1], tic);
            Console.WriteLine("#Ions>=charge{0}:\t{1}", args[2], tms);
        }
    }
}
