using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace trxParser
{
    class Program
    {
        private static void Main(string[] args)
        {
            string dir = args.FirstOrDefault() ??
                @"D:\src\Artifact2\bin\Debug.AnyCPU\BlobStore.Service.L0\MS.VS.Services.BlobStore.Server.L0.Tests\TestResults\";

            var testMethods = new ConcurrentDictionary<Guid, string>();
            var testResults = new ConcurrentDictionary<string, int>();
            var testFails = new ConcurrentDictionary<string, int>();


            foreach (FileInfo trxPath in (new DirectoryInfo(dir)).EnumerateFiles("*.trx", SearchOption.TopDirectoryOnly)
                )
            {
                TestRunType testRun;

                using (var fileStreamReader = new StreamReader(trxPath.FullName))
                {
                    var xmlSer = new XmlSerializer(typeof (TestRunType));
                    testRun = (TestRunType) xmlSer.Deserialize(fileStreamReader);
                }

                foreach (var testDefs in testRun.Items.OfType<TestDefinitionType>())
                {
                    foreach (var unitTestDef in testDefs.Items.OfType<UnitTestType>())
                    {
                        testMethods[Guid.Parse(unitTestDef.id)] = unitTestDef.TestMethod.className;
                        testResults.TryAdd(unitTestDef.TestMethod.className, 0);
                        testFails.TryAdd(unitTestDef.TestMethod.className, 0);
                    }
                }

                foreach (var results in testRun.Items.OfType<ResultsType>())
                {
                    foreach (var unitTestResult in results.Items.OfType<UnitTestResultType>())
                    {
                        Guid testId = Guid.Parse(unitTestResult.testId);
                        string className = testMethods[testId];
                        if (unitTestResult.outcome == "Passed")
                        {
                            testResults[className]++;
                        }
                        else
                        {
                            testFails[className]++;

                            var output = unitTestResult.Items.OfType<OutputType>().Single();
                            string message = null;
                            string trace = null;
                            try
                            {
                                message = ((System.Xml.XmlNode[])output.ErrorInfo.Message)[0].Value;
                            }
                            catch (Exception)
                            {
                            }

                            try
                            {
                                trace = ((System.Xml.XmlNode[])output.ErrorInfo.StackTrace)[0].Value;
                            }
                            catch (Exception)
                            {
                            }
                            Console.WriteLine(className.Split('.').Last());
                            Console.WriteLine(message);
                            Console.WriteLine(trace);
                        }
                    }
                }
            }

            foreach (var kvp in testResults)
            {
                Console.WriteLine("{0}: {1} {2} ", kvp.Key.Split('.').Last(), kvp.Value, testFails[kvp.Key]);
            }
        }
    }
}
