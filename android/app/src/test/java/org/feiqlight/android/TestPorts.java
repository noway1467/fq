package org.feiqlight.android;

import java.io.IOException;
import java.net.BindException;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.ServerSocket;

public final class TestPorts {
    private TestPorts() { }

    public static int chat() throws IOException { return find(false); }
    public static int withTools() throws IOException { return find(true); }

    private static int find(boolean tools) throws IOException {
        InetAddress local = InetAddress.getByName("127.0.0.1");
        BindException last = null;
        int attempts = tools ? (45000 - 21000) / 3 : 32;
        for (int attempt = 0; attempt < attempts; attempt++) {
            int candidate = tools ? 21000 + 3 * attempt : 0;
            try (ServerSocket chat = new ServerSocket(candidate, 1, local);
                 DatagramSocket udp = new DatagramSocket(chat.getLocalPort(), local)) {
                int port = chat.getLocalPort();
                if (!tools) return port;
                try (ServerSocket sharing = new ServerSocket(port + 1, 1, local);
                     ServerSocket remote = new ServerSocket(port + 2, 1, local)) {
                    return port;
                }
            } catch (BindException occupied) {
                last = occupied;
            }
        }
        throw new IOException("未找到可同时绑定的回环测试端口", last);
    }
}
