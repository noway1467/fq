package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.nio.file.Files;
import java.util.*;
import javax.net.ssl.SSLSocket;

public final class TlsFilePeer {
    private final SecureIdentity identity;
    private final Set<String> receivers=java.util.concurrent.ConcurrentHashMap.newKeySet();
    public final LanNode.Peer peer;
    public volatile long offset=-1;

    public TlsFilePeer(ServerSocket listener)throws IOException {
        if(!listener.getInetAddress().isLoopbackAddress())throw new IOException("测试仅使用回环地址");
        identity=new SecureIdentity(Files.createTempDirectory("tls-file-peer-").toFile());
        peer=new LanNode.Peer((InetSocketAddress)listener.getLocalSocketAddress(),"source","host","发送方","");
        peer.deviceId=UUID.randomUUID().toString().replace("-","");peer.fingerprint=identity.fingerprint;
    }
    public static void enable(LanNode node)throws IOException {
        node.setDeviceId(UUID.randomUUID().toString().replace("-",""));
        node.configureSecurity(Files.createTempDirectory("tls-client-").toFile(),(name,code)->true);
    }
    public void authorize(LanNode node)throws IOException {
        if(node.security()==null)enable(node);
        receivers.add(node.security().fingerprint);
    }
    public SSLSocket open(Socket raw,Protocol.Attachment file)throws IOException {
        raw.setSoTimeout(5000);byte[] prefix=new byte[4];new DataInputStream(raw.getInputStream()).readFully(prefix);
        if(!Arrays.equals(prefix,new byte[]{'F','Q','2','\n'}))throw new IOException("文件请求没有使用 TLS");
        SSLSocket tls=identity.server(raw,true);
        InputStream input=tls.getInputStream();OutputStream output=tls.getOutputStream();
        Protocol.Packet hello=Protocol.parse(SecureIdentity.readFrame(input,Protocol.HELLO_FRAME_BYTES));
        String pin=SecureIdentity.remoteFingerprint(tls);
        if(hello==null||!pin.equals(hello.fingerprint)||!receivers.contains(pin))throw new IOException("不是本用例的接收节点");
        SecureIdentity.writeFrame(output,Protocol.encode(0,peer.login,peer.host,Protocol.ENTRY|Protocol.UTF8,peer.name,"",peer.deviceId,null,peer.fingerprint));
        if(!"YES".equals(SecureIdentity.readText(input,16)))throw new IOException("配对失败");SecureIdentity.writeText(output,"YES");
        if(!SecureIdentity.readText(input,128).startsWith("F\n"))throw new IOException("非文件操作");
        Protocol.Packet request=Protocol.parse(SecureIdentity.readFrame(input,60000));
        if(request==null||request.mode()!=Protocol.GET_FILE||!pin.equals(request.fingerprint))throw new IOException("无效请求");
        offset=Long.parseLong(request.body.split(":")[2],16);
        if(offset<0||offset>file.size)throw new IOException("越界偏移");
        SecureIdentity.writeText(output,"OK\n"+file.size+"\n"+file.sha256);return tls;
    }
}
