using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Onyx.XPatch.Console.Repository;
using Onyx.XPatch.Console.xml;

namespace Onyx.XPatch.Console
{
    static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var outputFolder = new DirectoryInfo(args[0]);

            args = args.Skip(1).ToArray();

            var programStopwatch = new Stopwatch();
            programStopwatch.Start();

            for (var i = 0; i < args.Length; i += 2)
            {
                var testcase = new FileInfo(args[i]);
                var transformation = new FileInfo(args[i + 1]);
                var testcaseStopwatch = new Stopwatch();

                testcaseStopwatch.Start();
                TransformTestcase(outputFolder, testcase, transformation);
                
                System.Console.WriteLine("Time elapsed: {0:g3}s", testcaseStopwatch.Elapsed.TotalSeconds);
            }

            System.Console.WriteLine("Total time elapsed: {0:g3}s", programStopwatch.Elapsed.TotalSeconds);
        }

        private static void TransformTestcase(DirectoryInfo outputFolder, FileInfo testcase, FileInfo transformationConfig)
        {
            System.Console.WriteLine("Processing {0} with transformation {1}", testcase.FullName, transformationConfig.FullName);

            // Load config
            var configXml = ReadConfig(transformationConfig);
            
            // Load file and convert
            var xmlDocument = XDocument.Load(testcase.FullName, LoadOptions.PreserveWhitespace);

            // Fix test document
            XmlChanger.ProcessDocument(xmlDocument, configXml.Elements);

            // Export the document
            var filePathNew = Path.Combine(outputFolder.FullName, testcase.Name);
            
            File.Delete(filePathNew);
            
            xmlDocument.Save(filePathNew, SaveOptions.DisableFormatting);
        }

        private static ConfigXML ReadConfig(FileInfo transformationConfig)
        {
            using (var xmlReader = XmlReader.Create(transformationConfig.FullName))
            {
                var config = (ConfigXML) new XmlSerializer(typeof (ConfigXML)).Deserialize(xmlReader);

                return config;
            }
        }
    }
}