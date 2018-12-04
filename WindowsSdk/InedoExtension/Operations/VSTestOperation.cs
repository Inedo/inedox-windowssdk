using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Inedo.Agents;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.IO;

namespace Inedo.Extensions.WindowsSdk.Operations
{
    [ScriptAlias("Execute-VSTest")]
    [DisplayName("Execute VSTest Tests")]
    [Description("Runs VSTest unit tests on a specified test project, recommended for tests in VS 2012 and later.")]
    public sealed class VSTestOperation : ExecuteOperation
    {
        [Required]
        [ScriptAlias("TestContainer")]
        [DisplayName("Test container")]
        public string TestContainer { get; set; }

        [ScriptAlias("Group")]
        [DisplayName("Test group")]
        [PlaceholderText("Unit Tests")]
        public string TestGroup { get; set; }

        [Category("Advanced")]
        [ScriptAlias("Arguments")]
        [DisplayName("Additional arguments")]
        public string AdditionalArguments { get; set; }

        [Category("Advanced")]
        [ScriptAlias("ClearExistingTestResults")]
        [DisplayName("Clear existing results")]
        [Description("When true, the test results directory will be cleared before the tests are run.")]
        public bool ClearExistingTestResults { get; set; }

        [Category("Advanced")]
        [ScriptAlias("VsTestPath")]
        [DisplayName("VSTest Path")]
        [DefaultValue("$VSTestExePath")]
        [Description(@"The path to vstest.console.exe, typically: <br /><br />"
    + @"%VSINSTALLDIR%\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe")]
        public string VsTestPath { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var vsTestPath = await this.GetVsTestPathAsync(context);
            if (string.IsNullOrEmpty(vsTestPath))
                return;

            var fileOps = await context.Agent.GetServiceAsync<IFileOperationsExecuter>();

            var containerPath = context.ResolvePath(this.TestContainer);
            var sourceDirectory = PathEx.GetDirectoryName(containerPath);
            var resultsPath = PathEx.Combine(sourceDirectory, "TestResults");

            if (this.ClearExistingTestResults)
            {
                this.LogDebug($"Clearing {resultsPath} directory...");
                await fileOps.ClearDirectoryAsync(resultsPath);
            }

            await this.ExecuteCommandLineAsync(
                context,
                new RemoteProcessStartInfo
                {
                    FileName = vsTestPath,
                    Arguments = $"\"{this.TestContainer}\" /logger:trx {this.AdditionalArguments}",
                    WorkingDirectory = sourceDirectory
                }
            );

            if (!await fileOps.DirectoryExistsAsync(resultsPath))
            {
                this.LogError("Could not find the generated \"TestResults\" directory after running unit tests at: " + sourceDirectory);
                return;
            }

            var trxFiles = (await fileOps.GetFileSystemInfosAsync(resultsPath, new MaskingContext(new[] { "*.trx" }, Enumerable.Empty<string>())))
                .OfType<SlimFileInfo>()
                .ToList();

            if (trxFiles.Count == 0)
            {
                this.LogError("There are no .trx files in the \"TestResults\" directory.");
                return;
            }

            var trxPath = trxFiles
                .Aggregate((latest, next) => next.LastWriteTimeUtc > latest.LastWriteTimeUtc ? next : latest)
                .FullName;

            XDocument doc;
            using (var file = await fileOps.OpenFileAsync(trxPath, FileMode.Open, FileAccess.Read))
            using (var reader = new XmlTextReader(file) { Namespaces = false })
            {
                doc = XDocument.Load(reader);
            }

            var testRecorder = await context.TryGetServiceAsync<IUnitTestRecorder>();

            foreach (var result in doc.Element("TestRun").Element("Results").Elements("UnitTestResult"))
            {
                var testName = (string)result.Attribute("testName");
                var outcome = (string)result.Attribute("outcome");
                var output = result.Element("Output");
                UnitTestStatus status;
                string testResult;

                if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
                {
                    status = UnitTestStatus.Passed;
                    testResult = "Passed";
                }
                else if (string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase))
                {
                    status = UnitTestStatus.Inconclusive;
                    if (output == null)
                        testResult = "Ignored";
                    else
                        testResult = GetResultTextFromOutput(output);
                }
                else
                {
                    status = UnitTestStatus.Failed;
                    testResult = GetResultTextFromOutput(output);
                }

                this.Log(
                    status == UnitTestStatus.Failed ? MessageLevel.Error : MessageLevel.Information,
                    $"{testName}: {testResult}"
                );

                if (testRecorder != null)
                {
                    var startDate = (DateTimeOffset)result.Attribute("startTime");
                    var duration = (TimeSpan)result.Attribute("duration");
                    await testRecorder.RecordUnitTestAsync(AH.CoalesceString(this.TestGroup, "Unit Tests"), testName, status, testResult, startDate, duration);
                }
            }
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            return new ExtendedRichDescription(
                new RichDescription(
                    "Run VSTest on ",
                    new DirectoryHilite(config[nameof(this.TestContainer)])
                )
            );
        }

        private async Task<string> GetVsTestPathAsync(IOperationExecutionContext context)
        {
            if (string.IsNullOrWhiteSpace(this.VsTestPath))
            {
                this.LogError("Unable to find vstest.console.exe. Verify that VSTest is installed and set a $VSTestExePath server variable to its full path.");
                return null;
            }
            else
            {
                this.LogDebug("VSTestExePath = " + this.VsTestPath);
                if (!await (await context.Agent.GetServiceAsync<IFileOperationsExecuter>()).FileExistsAsync(this.VsTestPath))
                {
                    this.LogError($"The file {this.VsTestPath} does not exist. Verify that VSTest is installed.");
                    return null;
                }

                return this.VsTestPath;
            }
        }

        private static string GetResultTextFromOutput(XElement output)
        {
            var message = string.Empty;
            var errorInfo = output.Element("ErrorInfo");
            if (errorInfo != null)
            {
                message = (string)errorInfo.Element("Message");
                var trace = (string)errorInfo.Element("StackTrace");
                if (!string.IsNullOrEmpty(trace))
                    message += Environment.NewLine + trace;
            }

            return message;
        }
    }
}
