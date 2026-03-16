using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Office.Interop.Excel;

namespace NewSanofi.ClassHelper
{
    public static class ImportExcel
    {
        public static List<string> import_start(string file)
        {
            List<string> result = new List<string>();
            try
            {
                Missing missing = Missing.Value;
                Application excel = (Application)Activator.CreateInstance(Marshal.GetTypeFromCLSID(new Guid("00024500-0000-0000-C000-000000000046")));
                Workbook xlWorkBook = excel.Workbooks.Open(file, false, true, missing, missing, missing, true, XlPlatform.xlWindows, '\t', false, false, 0, false, true, 0);
                Worksheet xlWorkSheet = (Worksheet)xlWorkBook.Worksheets.get_Item((object)1);
                Range xlRange = xlWorkSheet.UsedRange;
                Array myValues = (Array)xlRange.Cells.Value2;
                int vertical = myValues.GetLength(0);
                for (int a = 1; a <= vertical; a++)
                {
                    try
                    {
                        result.Add(myValues.GetValue(a, 1).ToString());
                    }
                    catch
                    {
                        break;
                    }
                }

                xlWorkBook.Close(false, missing, missing);
                excel.Quit();
                releaseObject(xlWorkSheet);
                releaseObject(xlWorkBook);
                releaseObject(excel);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static void releaseObject(object obj)
        {
            try
            {
                Marshal.ReleaseComObject(obj);
                obj = null;
            }
            catch
            {
                obj = null;
            }
            finally
            {
                GC.Collect();
            }
        }
    }
}
