using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using VirtualClient.Actions;
using VirtualClient.Contracts;

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Parser
{
    [TestFixture]
    [Category("Unit")]
    public class BombardierMetricsParserTests
    {
        private string rawText;
        private BombardierMetricsParser testParser;

        private string ExamplePath
        {
            get
            {
                string workingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(workingDirectory, "Examples", "Bombardier");
            }
        }

        [Test]
        public void BombardierParserVerifyMetrics()
        {
            string outputPath = Path.Combine(this.ExamplePath, "BombardierExample.txt");
            this.rawText = File.ReadAllText(outputPath);
            this.testParser = new BombardierMetricsParser(this.rawText);
            IList<Metric> metrics = this.testParser.Parse();

            Assert.AreEqual(16, metrics.Count);
            MetricAssert.Exists(metrics, "Latency Max", 178703, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency Average", 8270.807963429836, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency Stddev", 6124.356473307014, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency P50", 6058, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency P75", 10913, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency P90", 17949, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency P95", 23318, MetricUnit.Microseconds);
            MetricAssert.Exists(metrics, "Latency P99", 35856, MetricUnit.Microseconds);

            MetricAssert.Exists(metrics, "RequestPerSecond Max", 67321.282458945348, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond Average", 31211.609987720527, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond Stddev", 6446.822354105378, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond P50", 31049.462844, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond P75", 35597.436614, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond P90", 39826.205746, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond P95", 41662.542962, MetricUnit.RequestsPerSec);
            MetricAssert.Exists(metrics, "RequestPerSecond P99", 49625.656227, MetricUnit.RequestsPerSec);
        }
    }
}