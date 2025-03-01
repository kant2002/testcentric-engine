// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

#if !NETCOREAPP2_1
using System;
using NUnit.Engine;

namespace TestCentric.Engine.Services
{
    public partial class AgentStoreTests
    {
        private sealed class DummyTestAgent : ITestAgent
        {
            public DummyTestAgent(Guid id)
            {
                Id = id;
            }

            public Guid Id { get; }

            public ITestEngineRunner CreateRunner(TestPackage package)
            {
                throw new NotImplementedException();
            }

            public bool Start()
            {
                throw new NotImplementedException();
            }

            public void Stop()
            {
                throw new NotImplementedException();
            }
        }
    }
}
#endif
