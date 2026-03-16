using System.Collections.Generic;
using System.IO;

namespace NewSanofi.ClassHelper
{
    public static class TextFileProcess
    {
        public static void AppendFile(string text, string line)
        {
            string filename = text + ".txt";
            string path = Path.Combine(MainWindow.currentDirectory, "Data\\", filename);
            if (!File.Exists(path))
            {
                using (StreamWriter sw = File.CreateText(path))
                {
                    sw.WriteLine(line);
                    return;
                }
            }

            using (StreamWriter sw2 = File.AppendText(path))
            {
                sw2.WriteLine(line);
            }
        }

        public static void DeleteFile(string text)
        {
            string filename = text + ".txt";
            string path = Path.Combine(MainWindow.currentDirectory, "Data\\", filename);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static void DeleteFile1(string text)
        {
            string path = Path.Combine(MainWindow.currentDirectory, text);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static List<string> ReadFile(string text)
        {
            List<string> lsString = new List<string>();
            string filename = text + ".txt";
            string path = Path.Combine(MainWindow.currentDirectory, "Data\\", filename);
            if (!File.Exists(path))
            {
                return lsString;
            }

            using (StreamReader sr = new StreamReader(path))
            {
                for (string line = sr.ReadLine(); line != null; line = sr.ReadLine())
                {
                    lsString.Add(line);
                }
            }

            return lsString;
        }

        public static List<string> ReadFileExtent(string path)
        {
            List<string> lsString = new List<string>();
            if (!File.Exists(path))
            {
                return lsString;
            }

            using (StreamReader sr = new StreamReader(path))
            {
                for (string line = sr.ReadLine(); line != null; line = sr.ReadLine())
                {
                    lsString.Add(line);
                }
            }

            return lsString;
        }

        public static void WriteFile(string fileName, List<string> content)
        {
            string filename = fileName + ".txt";
            string tempPath = Path.Combine(MainWindow.currentDirectory, "Data\\");
            string path = Path.Combine(MainWindow.currentDirectory, "Data\\", filename);
            if (!Directory.Exists(tempPath))
            {
                Directory.CreateDirectory(tempPath);
            }

            using (StreamWriter sw = new StreamWriter(path))
            {
                foreach (string c in content)
                {
                    sw.WriteLine(c);
                }
            }
        }
    }
}
