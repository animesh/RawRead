using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

using MSFileReaderLib;  /* add reference XRawfile2_x64.dll from 28.9 MB	MSFileReader2.2.zip available at https://thermo.flexnetoperations.com/control/thmo/login, then logging in and choosing "Utility Software".  */

namespace RawRead
{
    class RawRead2PeakList
    {
        static void Main(string[] args)
        {
            MSFileReader_XRawfile rawMSfile = new MSFileReader_XRawfile();
            rawMSfile.Open("F:\\promec\\Davi\\_QE\\BSAs\\20150512_BSA_The-PEG-envelope.raw");
            rawMSfile.SetCurrentController(0, 1); /* Controller type 0 means mass spec device; Controller 1 means first MS device */
            int nms = 0; /* ref int usrd with GetNumSpectra */
            int pbMSData = 0;
            int pnLastSpectrum = 0;
            rawMSfile.GetNumSpectra(ref nms);
            rawMSfile.IsThereMSData(ref pbMSData);
            rawMSfile.GetLastSpectrumNumber(ref pnLastSpectrum);
            Debug.WriteLine("Total Spectra: " + nms);
            Debug.WriteLine("MSdata: " + pbMSData);
            Debug.WriteLine("Last MSdata: " + pnLastSpectrum);
            double pkWidCentroid = 0.0;
            object mzList = null;
            object pkFlg = null;
            int arrLen = 0;
            for (int i = 0; i < nms; i++)
            {
                rawMSfile.GetMassListFromScanNum(i, null, 1, 0, 0, 0, ref pkWidCentroid, ref mzList, ref pkFlg, ref arrLen);
                double[,] mslist = (double[,])mzList;
                double dMass = 0;
                double dIntensity = 0;
                for (long j = 0; j < arrLen; j++)
                {
                    dMass = mslist[0,j];
                    dIntensity = mslist[1,j];
                    Debug.WriteLine("Scan:" + i + "mass: " + dMass + "intensity: " + dIntensity);
                }
                Console.WriteLine("Scan:" + i + "MZ:" + arrLen + "mass: " + dMass + "intensity: " + dIntensity);
            }
        }
    }
}

/* http://bioinfo.kouwua.net/2012/09/read-thermo-raw-with-msfilereader-in-c.html
 * http://tools.thermofisher.com/content/sfs/manuals/Man-XCALI-97542-MSFileReader-30-Ref-ManXCALI97542-A-EN.pdf 
  example for GetMassListFromScanNum
typedef struct _datapeak
{
    double dMass;
    double dIntensity;
}
DataPeak;
long nScanNumber = 12; // read the contents of scan 12
VARIANT varMassList;
VariantInit(&varMassList);
VARIANT varPeakFlags;
VariantInit(&varPeakFlags);
long nArraySize = 0;
long nRet = XRawfileCtrl.GetMassListFromScanNum(&nScanNumber,
NULL, 
0, 
0, 
0,
FALSE, 
& varMassList, 
& varPeakFlags,
& nArraySize); 
if( nRet != 0 )
{
::MessageBox(NULL, _T(“Error getting mass list data for scan 12.”), _T(“Error”), 
MB_OK );
}
if( nArraySize )
{
SAFEARRAY FAR* psa = varMassList.parray;
DataPeak* pDataPeaks = NULL;
SafeArrayAccessData(psa, (void**)(&pDataPeaks) );
for( long j = 0; j<nArraySize; j++ )
{
double dMass = pDataPeaks[j].dMass;
double dIntensity = pDataPeaks[j].dIntensity;
}
SafeArrayUnaccessData(psa );
}
if( varMassList.vt != VT_EMPTY )
{
SAFEARRAY FAR* psa = varMassList.parray;
varMassList.parray = NULL;
// Delete the SafeArray
SafeArrayDestroy(psa );
}
if(varPeakFlags.vt != VT_EMPTY )
{
SAFEARRAY FAR* psa = varPeakFlags.parray;
varPeakFlags.parray = NULL;
// Delete the SafeArray
SafeArrayDestroy(psa );
}
*/


