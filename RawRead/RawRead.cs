namespace RawRead{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.ExceptionServices;
    using ThermoFisher.CommonCore.Data;
    using ThermoFisher.CommonCore.Data.Business;
    using ThermoFisher.CommonCore.Data.FilterEnums;
    using ThermoFisher.CommonCore.Data.Interfaces;
    using ThermoFisher.CommonCore.MassPrecisionEstimator;
    using ThermoFisher.CommonCore.RawFileReader;  /* RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader https://mail.google.com/mail/?view=cm&fs=1&tf=1&to=jim.Shofstahl@thermofisher.com&su=Access%20to%20RawFileReader%20from%20Planet%20Orbitrap */

    class RawRead2PeakList{
        static void Main(string[] args){
            var rawFile = RawFileReaderAdapter.FileFactory(args[0]);
            rawFile.SelectInstrument(Device.MS, 1);
            int fms = rawFile.RunHeaderEx.FirstSpectrum;
            int nms = rawFile.RunHeaderEx.LastSpectrum;
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#file:\t" + rawFile.FileName);
            Console.WriteLine("#ms:\t" + nms);
            Console.WriteLine("#RT:\t" + (eTime-sTime));
            Console.WriteLine("#version:\t" + rawFile.FileHeader.Revision);
            Console.WriteLine("#create date:\t" + rawFile.FileHeader.CreationDate);
            Console.WriteLine("#machine:\t" + rawFile.FileHeader.WhoCreatedId);
            Console.WriteLine("#serial#:\t" + rawFile.GetInstrumentData().SerialNumber);
            Console.WriteLine("#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion);
            Console.WriteLine("#resolution\t:{0:F3}", rawFile.RunHeaderEx.MassResolution);
            Console.WriteLine("#range:\t{0:F4}\t{1:F4}", rawFile.RunHeaderEx.LowMass, rawFile.RunHeaderEx.HighMass);
            for (int i = fms; i < nms; i++){
                var centroidStream = rawFile.GetCentroidStream(i, false);
                Console.WriteLine("{0}\t{1}\t", i, centroidStream.Length);
                for (int j = 0; j < centroidStream.Length; j++){
                    Console.WriteLine("\t\t{0}\t{1:F4}\t{2:F0}\t{3:F0}", j, centroidStream.Masses[j], centroidStream.Intensities[j], centroidStream.Charges[j]);
                }
            }
        }
    }
}
