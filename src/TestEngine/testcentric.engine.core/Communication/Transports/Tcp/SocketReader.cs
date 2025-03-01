// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using TestCentric.Engine.Communication.Messages;
using TestCentric.Engine.Communication.Protocols;

namespace TestCentric.Engine.Communication.Transports.Tcp
{
    public class SocketReader
    {
        private const int BUFFER_SIZE = 1024;

        private Socket _socket;
        private ISerializationProtocol _wireProtocol;

        private Queue<TestEngineMessage> _msgQueue;
        private byte[] _buffer;

        public SocketReader(Socket socket, ISerializationProtocol protocol)
        {
            _socket = socket;
            _wireProtocol = protocol;

            _msgQueue = new Queue<TestEngineMessage>();
            _buffer = new byte[BUFFER_SIZE];
        }

        /// <summary>
        /// Get the next TestEngineMessage to arrive
        /// </summary>
        /// <returns>The message</returns>
        public TestEngineMessage GetNextMessage()
        {
            while (_msgQueue.Count == 0)
            {
                int n = _socket.Receive(_buffer);
                var bytes = new byte[n];
                Array.Copy(_buffer, 0, bytes, 0, n);
                foreach (var message in _wireProtocol.Decode(bytes))
                    _msgQueue.Enqueue(message);
            }

            return _msgQueue.Dequeue();
        }

        /// <summary>
        /// Get the next message to arrive, which must be of the
        /// specified message type.
        /// </summary>
        /// <typeparam name="TMessage">The expected message type</typeparam>
        /// <returns>A message of type TMessage</returns>
        /// <exception cref="InvalidOperationException">A message of a different type was received</exception>
        public TMessage GetNextMessage<TMessage>() where TMessage : TestEngineMessage
        {
            var receivedMessage = GetNextMessage();
            var expectedMessage = receivedMessage as TMessage;

            if (expectedMessage == null)
                throw new InvalidOperationException($"Expected a {typeof(TMessage)} but received a {receivedMessage.GetType()}");

            return expectedMessage;
        }
    }
}
