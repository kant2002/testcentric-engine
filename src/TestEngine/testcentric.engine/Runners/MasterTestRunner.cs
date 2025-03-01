// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml;
using NUnit.Engine;
using TestCentric.Engine.Internal;
using TestCentric.Engine.Services;

namespace TestCentric.Engine.Runners
{
    /// <summary>
    /// MasterTestRunner implements the ITestRunner interface, which
    /// is the user-facing representation of a test runner. It uses
    /// various internal runners to load and run tests for the user.
    /// </summary>
    public class MasterTestRunner : ITestRunner
    {
        // MasterTestRunner is the only runner that is passed back
        // to users asking for an ITestRunner. The actual details of
        // execution are handled by various internal runners, which
        // impement ITestEngineRunner.
        //
        // Explore and execution results from MasterTestRunner are
        // returned as XmlNodes, created from the internal 
        // TestEngineResult representation.
        // 
        // MasterTestRUnner is responsible for creating the test-run
        // element, which wraps all the individual assembly and project
        // results.

        private ITestEngineRunner _engineRunner;
        private readonly IServiceLocator _services;
        private readonly TestPackageAnalyzer _packageAnalyzer;
        private readonly IRuntimeFrameworkService _runtimeService;
        private readonly IProjectService _projectService;
        private ITestRunnerFactory _testRunnerFactory;
        private bool _disposed;

        private TestEventDispatcher _eventDispatcher;

        private const int WAIT_FOR_CANCEL_TO_COMPLETE = 5000;

        public MasterTestRunner(IServiceLocator services, TestPackage package)
        {
            if (services == null) throw new ArgumentNullException("services");
            if (package == null) throw new ArgumentNullException("package");

            _services = services;
            TestPackage = package;

            // Get references to the services we use
            _projectService = _services.GetService<IProjectService>();
            _testRunnerFactory = _services.GetService<ITestRunnerFactory>();

            _packageAnalyzer = _services.GetService<TestPackageAnalyzer>();
            _runtimeService = _services.GetService<IRuntimeFrameworkService>();

            _eventDispatcher = _services.GetService<TestEventDispatcher>();

            // Last chance to catch invalid settings in package,
            // in case the client runner missed them.
            _packageAnalyzer.ValidatePackageSettings(package);
        }

        /// <summary>
        /// The TestPackage for which this is the runner
        /// </summary>
        protected TestPackage TestPackage { get; set; }

        /// <summary>
        /// The result of the last call to LoadPackage
        /// </summary>
        protected TestEngineResult LoadResult { get; set; }

        /// <summary>
        /// Gets an indicator of whether the package has been loaded.
        /// </summary>
        protected bool IsPackageLoaded
        {
            get { return LoadResult != null; }
        }

        /// <summary>
        /// Get a flag indicating whether a test is running
        /// </summary>
        public bool IsTestRunning { get; private set; }

        /// <summary>
        /// Load a TestPackage for possible execution. The
        /// explicit implementation returns an ITestEngineResult
        /// for consumption by clients.
        /// </summary>
        /// <returns>An XmlNode representing the loaded assembly.</returns>
        public XmlNode Load()
        {
            LoadResult = PrepareResult(GetEngineRunner().Load()).MakeTestRunResult(TestPackage);

            return LoadResult.Xml;
        }

        /// <summary>
        /// Unload any loaded TestPackage. If none is loaded,
        /// the call is ignored.
        /// </summary>
        public void Unload()
        {
            UnloadPackage();
        }

        /// <summary>
        /// Reload the currently loaded test package.
        /// </summary>
        /// <returns>An XmlNode representing the loaded package</returns>
        /// <exception cref="InvalidOperationException">If no package has been loaded</exception>
        public XmlNode Reload()
        {
            LoadResult = PrepareResult(GetEngineRunner().Reload()).MakeTestRunResult(TestPackage);

            return LoadResult.Xml;
        }

        /// <summary>
        /// Count the test cases that would be run under the specified
        /// filter, loading the TestPackage if it is not already loaded.
        /// </summary>
        /// <param name="filter">A TestFilter</param>
        /// <returns>The count of test cases.</returns>
        public int CountTestCases(TestFilter filter)
        {
            return GetEngineRunner().CountTestCases(filter);
        }

        /// <summary>
        /// Run the tests in a loaded TestPackage. The explicit
        /// implementation returns an ITestEngineResult for use
        /// by external clients.
        /// </summary>
        /// <param name="listener">An ITestEventHandler to receive events</param>
        /// <param name="filter">A TestFilter used to select tests</param>
        /// <returns>An XmlNode giving the result of the test execution</returns>
        public XmlNode Run(ITestEventListener listener, TestFilter filter)
        {
            return RunTests(listener, filter).Xml;
        }

        /// <summary>
        /// Start a run of the tests in the loaded TestPackage. The tests are run
        /// asynchronously and the listener interface is notified as it progresses.
        /// </summary>
        /// <param name="listener">The listener that is notified as the run progresses</param>
        /// <param name="filter">A TestFilter used to select tests</param>
        /// <returns></returns>
        public ITestRun RunAsync(ITestEventListener listener, TestFilter filter)
        {
            return RunTestsAsync(listener, filter);
        }

        /// <summary>
        /// Cancel the ongoing test run. If no  test is running, the call is ignored.
        /// </summary>
        /// <param name="force">If true, cancel any ongoing test threads, otherwise wait for them to complete.</param>
        public void StopRun(bool force)
        {
            _engineRunner.StopRun(force);

            if (force)
            {
                // Frameworks should handle StopRun(true) by cancelling all tests and notifying
                // us of the completion of any tests that were running. However, this feature
                // may be absent in some frameworks or may be broken and we may not pass on the
                // notifications needed by some runners. In fact, such a bug is present in the
                // NUnit framework through release 3.12 and motivated the following code.
                //
                // We try to make up for the potential problem here by notifying the listeners 
                // of the completion of every pending WorkItem, that is, one that started but
                // never sent a completion event.

                if (!_eventDispatcher.WaitForCompletion(WAIT_FOR_CANCEL_TO_COMPLETE))
                {
                    _eventDispatcher.IssuePendingNotifications();

                    // Signal completion of the run
                    _eventDispatcher.OnTestEvent($"<test-run id='{TestPackage.ID}' result='Failed' label='Cancelled' />");

                    // Since we were not notified of the completion of some items, we can't trust
                    // that they were actually stopped by the framework. To make sure nothing is
                    // left running, we unload the tests. By unloading only the lower-level engine
                    // runner and not the MasterTestRunner itself, we allow the tests to be loaded
                    // for subsequent runs using the same package.

                    _engineRunner.Unload();
                }
            }
        }

        /// <summary>
        /// Explore a loaded TestPackage and return information about
        /// the tests found.
        /// </summary>
        /// <param name="filter">A TestFilter used to select tests</param>
        /// <returns>An XmlNode representing the tests found.</returns>
        public XmlNode Explore(TestFilter filter)
        {
            LoadResult = PrepareResult(GetEngineRunner().Explore(filter))
                .MakeTestRunResult(TestPackage);

            return LoadResult.Xml;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose of this object.
        /// </summary>
        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing && _engineRunner != null)
                    _engineRunner.Dispose();

                _disposed = true;
            }
        }

        //Exposed for testing
        internal ITestEngineRunner GetEngineRunner()
        {
            if (_engineRunner == null)
            {
                // Expand any project subpackages
                _packageAnalyzer.ExpandProjectPackages(TestPackage);

                // Add package settings to reflect the target runtime
                // and test framework usage of each assembly.
                foreach (var package in TestPackage.Select(p => p.IsAssemblyPackage()))
                    if (File.Exists(package.FullName))
                        _packageAnalyzer.ApplyImageSettings(package);

                // Use SelectRuntimeFramework for its side effects.
                // Info will be left behind in the package about
                // each contained assembly, which will subsequently
                // be used to determine how to run the assembly.
                _runtimeService.SelectRuntimeFramework(TestPackage);

                _engineRunner = _testRunnerFactory.MakeTestRunner(TestPackage);
            }

            return _engineRunner;
        }

        // The TestEngineResult returned to MasterTestRunner contains no info
        // about projects. At this point, if there are any projects, the result
        // needs to be modified to include info about them. Doing it this way
        // allows the lower-level runners to be completely ignorant of projects
        private TestEngineResult PrepareResult(TestEngineResult result)
        {
            if (result == null) throw new ArgumentNullException("result");

            // See if we have any projects to deal with. At this point,
            // any subpackage, which itself has subpackages, is a project
            // we expanded.
            bool hasProjects = false;
                foreach (var p in TestPackage.SubPackages)
                    hasProjects |= p.HasSubPackages();

            // If no Projects, there's nothing to do
            if (!hasProjects)
                return result;

            // If there is just one subpackage, it has to be a project and we don't
            // need to rebuild the XML but only wrap it with a project result.
            if (TestPackage.SubPackages.Count == 1)
                return result.MakeProjectResult(TestPackage.SubPackages[0]);

            // Most complex case - we need to work with the XML in order to
            // examine and rebuild the result to include project nodes.
            // NOTE: The algorithm used here relies on the ordering of nodes in the
            // result matching the ordering of subpackages under the top-level package.
            // If that should change in the future, then we would need to implement
            // identification and summarization of projects into each of the lower-
            // level TestEngineRunners. In that case, we will be warned by failures
            // of some of the MasterTestRunnerTests.

            // Start a fresh TestEngineResult for top level
            var topLevelResult = new TestEngineResult();
            int nextTest = 0;

            foreach (var subPackage in TestPackage.SubPackages)
            {
                if (subPackage.HasSubPackages())
                {
                    // This is a project, create an intermediate result
                    var projectResult = new TestEngineResult();
                    
                    // Now move any children of this project under it. As noted
                    // above, we must rely on ordering here because (1) the
                    // fullname attribute is not reliable on all nunit framework
                    // versions, (2) we may have duplicates of the same assembly
                    // and (3) we have no info about the id of each assembly.
                    int numChildren = subPackage.SubPackages.Count;
                    while (numChildren-- > 0)
                        projectResult.Add(result.XmlNodes[nextTest++]);
                    
                    topLevelResult.Add(projectResult.MakeProjectResult(subPackage).Xml);
                }
                else
                {
                    // Add the next assembly package to our new result
                    topLevelResult.Add(result.XmlNodes[nextTest++]);
                }
            }
            
            return topLevelResult;
        }

        /// <summary>
        /// Unload any loaded TestPackage.
        /// </summary>
        private void UnloadPackage()
        {
            LoadResult = null;
            if (_engineRunner != null)
                _engineRunner.Unload();
        }

        /// <summary>
        /// Count the test cases that would be run under
        /// the specified filter. Returns zero if the
        /// package has not yet been loaded.
        /// </summary>
        /// <param name="filter">A TestFilter</param>
        /// <returns>The count of test cases</returns>
        private int CountTests(TestFilter filter)
        {
            if (!IsPackageLoaded) return 0;

            return GetEngineRunner().CountTestCases(filter);
        }

        /// <summary>
        /// Run the tests in the loaded TestPackage and return a test result. The tests
        /// are run synchronously and the listener interface is notified as it progresses.
        /// </summary>
        /// <param name="listener">An ITestEventHandler to receive events</param>
        /// <param name="filter">A TestFilter used to select tests</param>
        /// <returns>A TestEngineResult giving the result of the test execution</returns>
        private TestEngineResult RunTests(ITestEventListener listener, TestFilter filter)
        {
            _eventDispatcher.ClearListeners();

            if (listener != null)
                _eventDispatcher.Listeners.Add(listener);

            IsTestRunning = true;
            
            string clrVersion;
            string engineVersion;

            clrVersion = Environment.Version.ToString();
            engineVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            var startTime = DateTime.UtcNow;
            long startTicks = Stopwatch.GetTimestamp();

            try
            {
                var startRunNode = XmlHelper.CreateTopLevelElement("start-run");
                startRunNode.AddAttribute("count", CountTests(filter).ToString());
                startRunNode.AddAttribute("start-time", XmlConvert.ToString(startTime, "u"));
                startRunNode.AddAttribute("engine-version", engineVersion);
                startRunNode.AddAttribute("clr-version", clrVersion);

                InsertCommandLineElement(startRunNode);

                _eventDispatcher.OnTestEvent(startRunNode.OuterXml);

                TestEngineResult result = PrepareResult(GetEngineRunner().Run(_eventDispatcher, filter)).MakeTestRunResult(TestPackage);

                // These are inserted in reverse order, since each is added as the first child.
                InsertFilterElement(result.Xml, filter);

                InsertCommandLineElement(result.Xml);

                result.Xml.AddAttribute("engine-version", engineVersion);
                result.Xml.AddAttribute("clr-version", clrVersion);
                double duration = (double)(Stopwatch.GetTimestamp() - startTicks) / Stopwatch.Frequency;
                result.Xml.AddAttribute("start-time", XmlConvert.ToString(startTime, "u"));
                result.Xml.AddAttribute("end-time", XmlConvert.ToString(DateTime.UtcNow, "u"));
                result.Xml.AddAttribute("duration", duration.ToString("0.000000", NumberFormatInfo.InvariantInfo));

                IsTestRunning = false;

                _eventDispatcher.OnTestEvent(result.Xml.OuterXml);

                return result;
            }
            catch(Exception ex)
            {
                IsTestRunning = false;

                var resultXml = XmlHelper.CreateTopLevelElement("test-run");
                resultXml.AddAttribute("id", TestPackage.ID);
                resultXml.AddAttribute("result", "Failed");
                resultXml.AddAttribute("label", "Error");
                resultXml.AddAttribute("engine-version", engineVersion);
                resultXml.AddAttribute("clr-version", clrVersion);
                double duration = (double)(Stopwatch.GetTimestamp() - startTicks) / Stopwatch.Frequency;
                resultXml.AddAttribute("start-time", XmlConvert.ToString(startTime, "u"));
                resultXml.AddAttribute("end-time", XmlConvert.ToString(DateTime.UtcNow, "u"));
                resultXml.AddAttribute("duration", duration.ToString("0.000000", NumberFormatInfo.InvariantInfo));

                _eventDispatcher.OnTestEvent(resultXml.OuterXml);
                _eventDispatcher.OnTestEvent($"<unhandled-exception message=\"{ex.Message}\" />");

                return new TestEngineResult(resultXml);
            }
        }

        private AsyncTestEngineResult RunTestsAsync(ITestEventListener listener, TestFilter filter)
        {
            var testRun = new AsyncTestEngineResult();

            using (var worker = new BackgroundWorker())
            {
                worker.DoWork += (s, ea) =>
                {
                    var result = RunTests(listener, filter);
                    testRun.SetResult(result);
                };

                worker.RunWorkerAsync();
            }

            return testRun;
        }

        private static void InsertCommandLineElement(XmlNode resultNode)
        {
            var doc = resultNode.OwnerDocument;

            if (doc == null)
            {
                return;
            }

            XmlNode cmd = doc.CreateElement("command-line");
            resultNode.InsertAfter(cmd, null);

            var cdata = doc.CreateCDataSection(Environment.CommandLine);
            cmd.AppendChild(cdata);
        }

        private static void InsertFilterElement(XmlNode resultNode, TestFilter filter)
        {
            // Convert the filter to an XmlNode
            var tempNode = XmlHelper.CreateXmlNode(filter.Text);

            // Don't include it if it's an empty filter
            if (tempNode.ChildNodes.Count <= 0)
            {
                return;
            }

            var doc = resultNode.OwnerDocument;
            if (doc == null)
            {
                return;
            }

            var filterElement = doc.ImportNode(tempNode, true);
            resultNode.InsertAfter(filterElement, null);
        }
    }
}
