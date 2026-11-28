using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace FeiqLight.Tests
{
    public static class LoopbackPorts
    {
        public static int Chat() { return Find(0, false); }
        public static int WithTools(int first = 18000) { return Find(first, true); }

        private static int Find(int first, bool tools)
        {
            SocketException last = null;
            int attempts = tools ? (45000 - first + 2) / 3 : 32;
            for (int attempt = 0; attempt < attempts; attempt++) {
                var listeners = new List<TcpListener>();
                try {
                    int candidate = tools ? first + attempt * 3 : 0;
                    var chat = new TcpListener(IPAddress.Loopback, candidate);
                    listeners.Add(chat);
                    chat.Start();
                    int port = ((IPEndPoint)chat.LocalEndpoint).Port;
                    using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port))) {
                        for (int offset = 1; tools && offset <= 2; offset++) {
                            var listener = new TcpListener(IPAddress.Loopback, port + offset);
                            listeners.Add(listener);
                            listener.Start();
                        }
                        return port;
                    }
                } catch (SocketException error) {
                    if (error.SocketErrorCode != SocketError.AddressAlreadyInUse && error.SocketErrorCode != SocketError.AccessDenied) throw;
                    last = error;
                } finally {
                    foreach (var listener in listeners) listener.Stop();
                }
            }
            throw new IOException("未找到可同时绑定的回环测试端口", last);
        }
    }
}
