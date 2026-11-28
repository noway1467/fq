package org.feiqlight.android;

import org.feiqlight.android.core.*;
import java.io.IOException;
import java.util.UUID;
import java.util.concurrent.*;

final class TlsInbox implements AutoCloseable {
    final LanNode node;
    final LanNode.Peer peer;
    volatile LanNode.Peer sender;
    private final BlockingQueue<LanNode.Message> incoming=new LinkedBlockingQueue<>();

    TlsInbox()throws IOException {
        node=new LanNode(TestPorts.chat(),true,"test","host","审查联系人","测试",new LanNode.Listener(){
            public void peersChanged(){} public void error(String text){}
            public void message(LanNode.Peer from,LanNode.Message message,boolean update){if(!update&&!message.outgoing){sender=from;incoming.add(message);}}
        });
        TlsFilePeer.enable(node);peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+node.port()),"test","host","审查联系人","测试");
        peer.deviceId=UUID.randomUUID().toString().replace("-","");node.setDeviceId(peer.deviceId);peer.fingerprint=node.security().fingerprint;peer.utf8=true;node.start();
    }
    LanNode.Message next()throws InterruptedException {
        LanNode.Message message=incoming.poll(5,TimeUnit.SECONDS);if(message==null)throw new AssertionError("未收到 TLS 消息");return message;
    }
    public void close(){node.close();}
}
