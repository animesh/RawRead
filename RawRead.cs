//released under GPL version 2 or later: sharma.animesh@gmail.com
//install mono and compile: mcs RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
//run: mono RawRead.exe <ThermoOrbitrapRawfileName> <intensityThreshold>(optional) <chargeThreshold>(optional)
//windows with dotnet: c:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe RawRead.cs /reference:ThermoFisher.CommonCore.RawFileReader.dll   /reference:ThermoFisher.CommonCore.Data.dll /reference:ThermoFisher.CommonCore.MassPrecisionEstimator.dll /reference:MathNet.Numerics.dll /reference:System.Numerics.dll
namespace RawRead
{
    using ThermoFisher.CommonCore.RawFileReader;//RawFileReader from Planet Orbitrap http://planetorbitrap.com/rawfilereader
    using ThermoFisher.CommonCore.Data.Business;//RawFileReader
    using MathNet.Numerics.IntegralTransforms;//nuget or dotnet https://www.nuget.org/packages/MathNet.Numerics/
  //  using System.Numerics;//https://www.nuget.org/packages/System.Runtime.Numerics/
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Linq;
    //Register-PackageSource -provider NuGet -name nugetRepository -location https://www.nuget.org/api/v2
    using Microsoft.ML.Probabilistic;
    using Microsoft.ML.Probabilistic.Distributions;
    using Microsoft.ML.Probabilistic.Models;
    using Microsoft.ML.Probabilistic.Math;
    using Range = Microsoft.ML.Probabilistic.Models.Range;
    internal class RawRead2PeakList
    {
        static void Main(string[] args)
        {
            //var rawFile = RawFileReaderAdapter.FileFactory("20150512_BSA_The-PEG-envelope.raw");
            //var rawFile = RawFileReaderAdapter.FileFactory("20200219_KKL_SARS_CoV2_pool1_F1.raw");
            var rawFile = RawFileReaderAdapter.FileFactory("171010_Ip_Hela_ugi.raw");
            if (args.Length < 1 || !File.Exists(args[0])) { Console.WriteLine("USAGE: {0} fileName intensityThreshold(for example 1000, optional) errorThreshold(for example 3 for upto 3 decimal points, optional), using default 171010_Ip_Hela_ugi.raw", AppDomain.CurrentDomain.FriendlyName);  }
            if(args.Length > 0){ rawFile = RawFileReaderAdapter.FileFactory(args[0]);}
            if(!rawFile.IsOpen) { Console.WriteLine("Raw file {1} is already Open, probably not finish writing", rawFile.FileError, args[0]); return; }
            if(rawFile.IsError) { Console.WriteLine("Error opening {1}, probably not proper orbitrap raw file? This program is tested only on Elite, QE and HF orbitrap raw files...", rawFile.FileError, args[0]); return; }
            long insThr = 1000;
            if (args.Length == 2) { insThr = long.Parse(args[1]); }
            int errTol = 3; // 445.12057 10ppm[445.11612-445.12502]
            if (args.Length == 3) { errTol = int.Parse(args[2]); }
            rawFile.SelectInstrument(Device.MS, 1);
            int fMS = rawFile.RunHeaderEx.FirstSpectrum;
            int nMS = rawFile.RunHeaderEx.LastSpectrum;
            var fFilter = rawFile.GetFilterForScanNumber(fMS);
            var nFilter = rawFile.GetFilterForScanNumber(nMS);
            //GetSpectrum(rawFile, fMS, fFilter.ToString(), false);
            double sTime = rawFile.RunHeaderEx.StartTime;
            double eTime = rawFile.RunHeaderEx.EndTime;
            Console.WriteLine("#filename:\t" + rawFile.FileName + "\n" + "#prescan(s):\t" + nMS + "\n" + "#RT length:\t" + (eTime - sTime) + "\n" + "#version:\t" + rawFile.FileHeader.Revision + "\n" + "#create date:\t" + rawFile.FileHeader.CreationDate + "\n" + "#machine:\t" + rawFile.FileHeader.WhoCreatedId + "\n" + "#serial:\t" + rawFile.GetInstrumentData().SerialNumber + "\n" + "#writer:\t" + rawFile.GetInstrumentData().SoftwareVersion + "\n" + "#resolution:\t" + rawFile.RunHeaderEx.MassResolution + "\n" + "#massrange:\t" + rawFile.RunHeaderEx.LowMass + "-" + rawFile.RunHeaderEx.HighMass + "\n" + "#sample:\t" + rawFile.SampleInformation.Vial + "\n" + "#volume:\t" + rawFile.SampleInformation.SampleVolume + "\n" + "#injection:\t" + rawFile.SampleInformation.InjectionVolume + "\n" + "#dilution:\t" + rawFile.SampleInformation.DilutionFactor + "\n" + "#filter:\t" + fFilter.ToString() + "\n" + "#filterN:\t" + nFilter.ToString());
            long tms = 0;
            long tic = 0;
            long maxIntSum = 0;
//            Complex[] samples = new Complex[nMS];
            StreamWriter writeMS1 = new StreamWriter(rawFile.FileName + ".intensityThreshold" + insThr + ".errTolDecimalPlace" + errTol + ".MS.txt");
            writeMS1.WriteLine("Scan\tContainScans\tBasePeak\tMaxIntensity\tRT\tMostIntenseMass2Charge\tCumulativeIntensity\tTotalScans");
            Dictionary<string, long> intMZ1 = new Dictionary<string, long>(); 
            for (int i = fMS; i <= nMS; i++)
            {
                double time = rawFile.RetentionTimeFromScanNumber(i);
                string title = string.Join(Environment.NewLine, rawFile.GetScanEventForScanNumber(i));
                var scanStatistics = rawFile.GetScanStatsForScanNumber(i);
                var centroidStream = rawFile.GetCentroidStream(i, false);
                long maxInt = 0;
                int k = 0;
                if (title.Contains(" ms ") && scanStatistics.BasePeakIntensity>insThr)
                {
                    for (int j = 0; j < centroidStream.Length; j++)
                    {
                        long ic = (long)centroidStream.Intensities[j];
                        if (ic > insThr)
                        {
                            string MZ1R = Math.Round((Double)centroidStream.Masses[j], errTol, MidpointRounding.AwayFromZero).ToString();
                            tic += ic;
                            tms++;
                            if (intMZ1.ContainsKey(MZ1R)) { intMZ1[MZ1R] += ic; }
                            else { intMZ1.Add(MZ1R, ic); }
                            if (ic >= maxInt) { maxInt = ic; maxIntSum += maxInt; k = j; }
                        }
                        //                      writeMS1.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}", tms, time, i, j, centroidStream.Masses[j], centroidStream.Intensities[j], scanStatistics.BasePeakMass, maxInt, tic, maxIntSum);
                    }
                    if (i%100==0) { Console.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}", i, centroidStream.Length, scanStatistics.BasePeakMass, centroidStream.Masses[k], maxInt, time, maxIntSum, tms); }
                    writeMS1.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}", i, centroidStream.Length, scanStatistics.BasePeakMass, maxInt, time, centroidStream.Masses[k], maxIntSum, tms);
                    //                    samples[i-1] = new Complex(scanStatistics.BasePeakMass,time);
                    //                if(i>1&&Math.Ceiling(samples[i-1].Real)<samples[i-2].Real){Console.WriteLine("{0}\t{1}\t{2}\t{3}", i, scanStatistics.BasePeakMass, maxIntSum,samples[i-2].Real-samples[i-1].Real);}
                }
            }
            /*            Fourier.Forward(samples, FourierOptions.NoScaling);
                        StreamWriter writeFFT = new StreamWriter(rawFile.FileName + ".intensity" + insThr + ".FFT.txt");
                        writeFFT.WriteLine("MZ\tRT\tangle\tmagnitude");
                        for (int i = fMS-1; i < nMS; i++)
                        {
                            double magnitude = (2.0 / nMS) * (Math.Abs(Math.Sqrt(Math.Pow(samples[i].Real, 2) + Math.Pow(samples[i].Imaginary, 2))));
                            double angle = Math.Atan(samples[i].Imaginary / samples[i].Real);
                            writeFFT.WriteLine("{0}\t{1}\t{2}\t{3}", samples[i].Real, samples[i].Imaginary, angle, magnitude);
                        }
            writeFFT.Close();
            */
            Console.WriteLine("#TIC>={0}intensity:\t{1}\t{2}", insThr, tms, tic);
            writeMS1.Close();
            StreamWriter writeMZ1R = new StreamWriter(rawFile.FileName + ".intensityThreshold" + insThr + ".errTolDecimalPlace" + errTol + ".MZ1R.txt");
            writeMZ1R.WriteLine("MZ\tsumIntensity");
            foreach (var element in intMZ1.OrderByDescending(y => y.Value)) { writeMZ1R.WriteLine("{0}\t{1}",element.Key,element.Value); }
            writeMZ1R.Close();
            rawFile.Dispose();
                        var winnerData = new[] { 0, 0, 0, 1, 3, 4 };
            var loserData = new[] { 1, 3, 4, 2, 1, 2 };

            // Define the statistical model as a probabilistic program
            var game = new Range(winnerData.Length);
            var player = new Range(winnerData.Concat(loserData).Max() + 1);
            var playerSkills = Variable.Array<double>(player);
            playerSkills[player] = Variable.GaussianFromMeanAndVariance(6, 9).ForEach(player);

            var winners = Variable.Array<int>(game);
            var losers = Variable.Array<int>(game);

            using (Variable.ForEach(game))
            {
                // The player performance is a noisy version of their skill
                var winnerPerformance = Variable.GaussianFromMeanAndVariance(playerSkills[winners[game]], 1.0);
                var loserPerformance = Variable.GaussianFromMeanAndVariance(playerSkills[losers[game]], 1.0);

                // The winner performed better in this game
                Variable.ConstrainTrue(winnerPerformance > loserPerformance);
            }

            // Attach the data to the model
            winners.ObservedValue = winnerData;
            losers.ObservedValue = loserData;

            // Run inference
            var inferenceEngine = new InferenceEngine();
            var inferredSkills = inferenceEngine.Infer<Gaussian[]>(playerSkills);

            // The inferred skills are uncertain, which is captured in their variance
            var orderedPlayerSkills = inferredSkills
               .Select((s, i) => new { Player = i, Skill = s })
               .OrderByDescending(ps => ps.Skill.GetMean());

            foreach (var playerSkill in orderedPlayerSkills)
            {
                Console.WriteLine($"Player {playerSkill.Player} skill: {playerSkill.Skill}");
            }

            // https://github.com/dotnet/infer/blob/master/src/Tutorials/LearningAGaussianWithRanges.cs
            double[] data = new double[100];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Rand.Normal(0, 1);
            }

            /* https://github.com/dotnet/infer/blob/master/src/Tutorials/LearningAGaussian.cs
            // Create mean and precision random variables
            Variable<double> mean = Variable.GaussianFromMeanAndVariance(0, 100).Named("mean");
            Variable<double> precision = Variable.GammaFromShapeAndScale(1, 1).Named("precision");

            Range dataRange = new Range(data.Length).Named("n");
            VariableArray<double> x = Variable.Array<double>(dataRange).Named("x");
            x[dataRange] = Variable.GaussianFromMeanAndPrecision(mean, precision).ForEach(dataRange);
            x.ObservedValue = data;

            InferenceEngine engine = new InferenceEngine();

            // Retrieve the posterior distributions
            Console.WriteLine("mean=" + data.ToString());
            Console.WriteLine("prec=" + x);
            Console.WriteLine("mean=" + engine.Infer(mean));
            Console.WriteLine("prec=" + engine.Infer(precision));
                        // Sample data from standard Gaussian
            double[] data = new double[100];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Rand.Normal(0, 1);
            }

            // Create mean and precision random variables
            Variable<double> mean = Variable.GaussianFromMeanAndVariance(0, 100).Named("mean");
            Variable<double> precision = Variable.GammaFromShapeAndScale(1, 1).Named("precision");

            for (int i = 0; i < data.Length; i++)
            {
                Variable<double> x = Variable.GaussianFromMeanAndPrecision(mean, precision).Named("x" + i);
                x.ObservedValue = data[i];
            }

            InferenceEngine engine = new InferenceEngine();

            // Retrieve the posterior distributions
            Console.WriteLine("mean=" + engine.Infer(mean));
            Console.WriteLine("prec=" + engine.Infer(precision));
            */
 
        }
    }
}
