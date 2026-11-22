import org.feiqlight.android.core.*;
import java.io.*;
import java.nio.file.*;
import java.net.Socket;
import java.util.*;
import java.util.concurrent.*;

public final class PartialInterop {
    private static void check(boolean value,String reason){if(!value)throw new AssertionError(reason);}
    private static LanNode.Message next(BlockingQueue<LanNode.Message> messages,String expected)throws Exception {
        LanNode.Message message=messages.poll(10,TimeUnit.SECONDS);check(message!=null&&message.text.equals(expected),"未收到 "+expected+"，实际="+(message==null?"超时":message.text));return message;
    }
    private static final class Invitation {
        LanNode.Peer peer;long packet;File part,saved;LanNode.Transfer transfer;
    }
    private static Object field(Object value,String name)throws Exception {
        java.lang.reflect.Field member=value.getClass().getDeclaredField(name);member.setAccessible(true);return member.get(value);
    }
    private static void interruptStream(LanNode node,long packet)throws Exception {
        List<Socket> sockets=new ArrayList<>();
        synchronized(field(node,"gate")) {
            Object offer=((Map<?,?>)field(node,"offers")).get(packet+":1");check(offer!=null,"普通断网前报价已结束");
            for(Object socket:(Set<?>)field(offer,"activeSockets"))sockets.add((Socket)socket);
        }
        check(!sockets.isEmpty(),"普通断网前没有真实发送连接");for(Socket socket:sockets)socket.close();
    }
    private static final class PausedSource implements LanNode.Source {
        final byte[] bytes=new byte[8*1024*1024];
        final String mode;
        final java.util.concurrent.atomic.AtomicInteger opens=new java.util.concurrent.atomic.AtomicInteger();
        final java.util.concurrent.atomic.AtomicLong openedOffset=new java.util.concurrent.atomic.AtomicLong();
        final CountDownLatch release=new CountDownLatch(1);
        volatile boolean pause=true;
        PausedSource(String mode){this.mode=mode;new Random(79).nextBytes(bytes);}
        public String key(){return mode;}public String name(){return mode+".bin";}public long size(){return bytes.length;}public long modified(){return 123000;}
        public InputStream open(long offset) {
            boolean downloading=opens.incrementAndGet()>1;
            if(downloading)openedOffset.set(offset);
            // 第一次读取用于生成报价摘要；仅真实发送在 128 KiB 处暂停，可靠截住活跃连接。
            return new InputStream(){int position=(int)offset;
                private void waitAtBoundary()throws IOException {
                    if(downloading&&pause&&position>=131072)try {
                        if(!release.await(15,TimeUnit.SECONDS))throw new IOException("互通测试未解除源流暂停");
                    }catch(InterruptedException e){Thread.currentThread().interrupt();throw new IOException(e);}
                }
                public int read()throws IOException{waitAtBoundary();return position>=bytes.length?-1:bytes[position++]&255;}
                public int read(byte[] target,int start,int count)throws IOException {
                    waitAtBoundary();if(position>=bytes.length)return -1;
                    int length=Math.min(count,bytes.length-position);System.arraycopy(bytes,position,target,start,length);position+=length;return length;
                }
            };
        }
    }
    public static void main(String[] args)throws Exception {
        Path root=Paths.get(args[2]);Files.createDirectories(root);BlockingQueue<LanNode.Message> messages=new LinkedBlockingQueue<>();BlockingQueue<Long> canceled=new LinkedBlockingQueue<>();
        java.util.concurrent.atomic.AtomicReference<Invitation> current=new java.util.concurrent.atomic.AtomicReference<>();java.util.concurrent.atomic.AtomicReference<Throwable> failure=new java.util.concurrent.atomic.AtomicReference<>();
        final LanNode[] holder=new LanNode[1];
        ConcurrentMap<Long,PausedSource> sources=new ConcurrentHashMap<>();
        ExecutorService downloads=Executors.newSingleThreadExecutor();
        LanNode.Listener listener=new LanNode.Listener(){
            public void peersChanged(){}public void error(String text){System.out.println("NOTICE "+text);}
            public void offerCanceled(LanNode.Peer peer,long packet){try{Invitation item=current.get();check(item!=null&&item.packet==packet&&item.peer.id().equals(peer.id()),"取消匹配了错误发送者");check(item.part.length()>0&&item.transfer!=null,"通知前没有活跃非空断点");item.transfer.close();canceled.add(packet);}catch(Throwable e){failure.set(e);}}
            public void message(LanNode.Peer peer,LanNode.Message message,boolean update){
                if(message.outgoing||update)return;
                try {
                    boolean silent=message.text.startsWith("PLEASE_CANCEL_SILENT:");
                    boolean interrupted=message.text.startsWith("PLEASE_INTERRUPT:");
                    if(silent||interrupted||message.text.startsWith("PLEASE_CANCEL:")) {
                        long packet=Long.parseLong(message.text.substring(silent?21:interrupted?17:14));
                        PausedSource source=sources.get(packet);check(source!=null,"Android 源流没有登记");
                        if(interrupted) {
                            interruptStream(holder[0],packet);source.pause=false;source.release.countDown();
                            holder[0].sendMessage(peer,"INTERRUPTED:"+packet,Collections.emptyList());
                        } else {
                            if(!silent)check(holder[0].hasActiveOutgoingTransfer(peer.id(),packet),"Android 中途取消前发送流已结束");
                            check(holder[0].cancelOutgoing(peer.id(),packet,"已送达",!silent),"Android 报价未撤销");source.release.countDown();
                            if(silent)holder[0].sendMessage(peer,"SILENT_CANCELED:"+packet,Collections.emptyList());
                        }
                        return;
                    }
                    messages.add(message);
                }catch(Throwable e){failure.set(e);}
            }
        };
        try(LanNode node=new LanNode(Integer.parseInt(args[0]),true,"android-partial","Android","Android 断点互通","test",listener)) {
            holder[0]=node;node.setDeviceId("0123456789abcdef0123456789abcdef");node.configureSecurity(root.resolve("android-security").toFile(),(name,code)->true);node.start();node.probe(LanNode.endpoint("127.0.0.1:"+args[1]));
            LanNode.Peer peer=null;long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);while(peer==null&&System.nanoTime()<deadline){for(LanNode.Peer item:node.peers())if(item.login.equals("windows-partial"))peer=item;Thread.sleep(10);}check(peer!=null,"Windows 未发现");node.verifyPeer(peer);
            LanNode.Peer remotePeer=peer;
            for(String mode:new String[]{"WINDOWS_NOTIFY","WINDOWS_REVOKED","WINDOWS_INTERRUPTED"}) {
                boolean silent=mode.equals("WINDOWS_REVOKED"),interrupted=mode.equals("WINDOWS_INTERRUPTED");
                node.sendMessage(peer,mode,Collections.emptyList());LanNode.Message offer=next(messages,mode+"_OFFER");check(offer.files.size()==2,"Windows 同批附件数量错误");
                File saved=root.resolve(mode+"-saved.txt").toFile(),destination=root.resolve(mode+"-pending.bin").toFile();node.receiveFile(peer,offer.number,offer.files.get(0),saved,new LanNode.Transfer(),null);
                File part=LanNode.partialFile(peer,offer.number,offer.files.get(1),destination);
                Invitation invitation=new Invitation();invitation.peer=peer;invitation.packet=offer.number;invitation.part=part;invitation.saved=saved;current.set(invitation);
                if(silent) {
                    Files.write(part.toPath(),new byte[128]);node.sendMessage(peer,"CANCEL_SILENT:"+offer.number,Collections.emptyList());next(messages,"SILENT_DONE:"+offer.number);
                    boolean revoked=false;try{node.receiveFile(peer,offer.number,offer.files.get(1),destination,new LanNode.Transfer(),null);}catch(LanNode.OfferRevokedException expected){revoked=true;}
                    check(revoked,"Windows 未返回 REVOKED");
                } else {
                    invitation.transfer=new LanNode.Transfer();
                    Future<?> download=downloads.submit(()->{node.receiveFile(remotePeer,offer.number,offer.files.get(1),destination,invitation.transfer,null);return null;});
                    long waitUntil=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);
                    while(part.length()==0&&!download.isDone()&&System.nanoTime()<waitUntil)Thread.sleep(1);
                    long startedOffset=part.length();check(startedOffset>0&&startedOffset<offer.files.get(1).size&&!download.isDone(),"没有截住 Windows→Android 非空中途断点");
                    node.sendMessage(peer,(interrupted?"INTERRUPT:":"CANCEL:")+offer.number,Collections.emptyList());
                    next(messages,(interrupted?"INTERRUPTED:":"CANCEL_DONE:")+offer.number);
                    if(!interrupted){Long packet=canceled.poll(8,TimeUnit.SECONDS);check(packet!=null&&packet==offer.number,"Windows TLS 取消通知未到达");}
                    boolean failed=false;
                    try{download.get(10,TimeUnit.SECONDS);}catch(ExecutionException e){check(e.getCause() instanceof IOException&&!(e.getCause() instanceof LanNode.OfferRevokedException),"中途流因无关错误失败："+e.getCause());failed=true;}
                    check(failed,"中途操作前下载已经完成");
                    if(interrupted) {
                        long offset=part.length();check(offset>0&&offset<offer.files.get(1).size&&!invitation.transfer.isCancelled()&&canceled.isEmpty(),"普通断网误清断点或收到取消");
                        node.receiveFile(peer,offer.number,offer.files.get(1),destination,new LanNode.Transfer(),null);
                        check(SecureIdentity.hashFile(destination).equals(offer.files.get(1).sha256),"Windows→Android 续传 SHA-256 不一致");
                        System.out.println("DETAIL Windows→Android 续传偏移="+offset);
                    } else check(invitation.transfer.isCancelled(),"取消通知没有终止活跃下载");
                    System.out.println("DETAIL Windows→Android 中途操作起点="+startedOffset);
                }
                check(failure.get()==null&&!part.exists()&&Files.readString(saved.toPath()).equals("已保存正式文件"),"Windows→Android 清理失败："+failure.get());
                node.sendMessage(peer,mode+"_OK",Collections.emptyList());System.out.println("PASS Windows→Android TLS "+mode+"，正式文件保留");
            }
            File small=root.resolve("android-saved.txt").toFile();Files.writeString(small.toPath(),"Android 正式文件");
            LanNode.Source savedSource=new LanNode.Source(){
                public String key(){return "android-saved";}public String name(){return small.getName();}public long size(){return small.length();}public long modified(){return small.lastModified();}
                public InputStream open(long offset)throws IOException{FileInputStream input=new FileInputStream(small);input.getChannel().position(offset);return input;}
            };
            for(String mode:new String[]{"ANDROID_NOTIFY","ANDROID_REVOKED","ANDROID_INTERRUPTED"}) {
                PausedSource source=new PausedSource(mode);LanNode.Message offer=node.sendSources(peer,Arrays.asList(savedSource,source));sources.put(offer.number,source);
                next(messages,mode+"_OK");check(failure.get()==null,"Android→Windows 失败："+failure.get());
                if(mode.equals("ANDROID_INTERRUPTED")){check(source.openedOffset.get()>0,"Android 发送源没有从非零偏移续传");System.out.println("DETAIL Android→Windows 发送源续传偏移="+source.openedOffset.get());}
                System.out.println("PASS Android→Windows TLS "+mode+"，正式文件保留");
            }
            node.sendMessage(peer,"DONE",Collections.emptyList());next(messages,"DONE_OK");System.out.println("ANDROID PARTIAL INTEROP: 6 passed, 0 failed");
        } finally {
            for(PausedSource source:sources.values())source.release.countDown();
            downloads.shutdownNow();check(downloads.awaitTermination(5,TimeUnit.SECONDS),"互通接收线程未退出");
        }
    }
}
