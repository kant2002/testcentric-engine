// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

using System;
using NUnit.Engine;

namespace TestCentric.Engine
{
    /// <summary>
    /// The ITestAgent interface is implemented by remote test agents.
    /// </summary>
    public interface ITestAgent
    {
        /// <summary>
        /// Gets a Guid that uniquely identifies this agent.
        /// </summary>
        Guid Id { get; }

        /// <summary>
        /// Starts the agent, performing any required initialization
        /// </summary>
        /// <returns>True if successful, otherwise false</returns>
        bool Start();

        /// <summary>
        /// Stops the agent, releasing any resources
        /// </summary>
        void Stop();

        /// <summary>
        ///  Creates a test runner
        /// </summary>
        ITestEngineRunner CreateRunner(TestPackage package);
    }
}
