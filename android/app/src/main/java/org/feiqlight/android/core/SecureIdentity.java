package org.feiqlight.android.core;

import java.io.*;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.math.BigInteger;
import java.security.*;
import java.security.cert.*;
import java.security.spec.PKCS8EncodedKeySpec;
import java.text.SimpleDateFormat;
import java.util.*;
import javax.net.ssl.*;

/** 标准 TLS 加密；用户核对两端配对码后才保存设备指纹，不信任广播提供的身份。 */
public final class SecureIdentity {
    public static final class TrustException extends IOException {private static final long serialVersionUID=1L;public TrustException(String message){super(message);}}
    public interface Confirmation { boolean confirm(String name,String code) throws IOException; }
    public Confirmation confirmation;
    private final File root;
    private final X509Certificate certificate;
    private final KeyManager[] keys;
    private final Properties trusted=new Properties();
    private final java.util.concurrent.locks.ReentrantLock pairingGate=new java.util.concurrent.locks.ReentrantLock();
    private int revision;
    private final java.util.concurrent.CopyOnWriteArrayList<java.util.function.Consumer<String>> revocations=new java.util.concurrent.CopyOnWriteArrayList<>();
    private static final java.util.concurrent.ScheduledExecutorService handshakes=java.util.concurrent.Executors.newSingleThreadScheduledExecutor(r->{Thread thread=new Thread(r,"feiq-tls-deadline");thread.setDaemon(true);return thread;});
    public final String fingerprint;
    public SecureIdentity(File root) throws IOException {
        this.root=root;
        try {
            Files.createDirectories(root.toPath());
            File identity=new File(root,"identity.pk8");
            PrivateKey privateKey;
            if(identity.isFile()) {
                try(DataInputStream input=new DataInputStream(new FileInputStream(identity))) {
                    int keyLength=input.readInt();if(keyLength<1||keyLength>8192)throw new IOException("设备密钥长度无效");
                    byte[] key=new byte[keyLength];input.readFully(key);
                    privateKey=KeyFactory.getInstance("RSA").generatePrivate(new PKCS8EncodedKeySpec(key));
                    int certLength=input.readInt();if(certLength<1||certLength>8192)throw new IOException("设备证书长度无效");
                    byte[] cert=new byte[certLength];input.readFully(cert);
                    certificate=(X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(cert));
                }
            } else {
                KeyPairGenerator generator=KeyPairGenerator.getInstance("RSA");generator.initialize(2048);KeyPair pair=generator.generateKeyPair();
                privateKey=pair.getPrivate();certificate=createCertificate(pair);
                ByteArrayOutputStream buffer=new ByteArrayOutputStream();try(DataOutputStream output=new DataOutputStream(buffer)) {byte[] key=privateKey.getEncoded(),cert=certificate.getEncoded();output.writeInt(key.length);output.write(key);output.writeInt(cert.length);output.write(cert);}
                atomic(identity,buffer.toByteArray());
            }
            fingerprint=hash(certificate.getEncoded());
            KeyStore store=KeyStore.getInstance("PKCS12");store.load(null,null);store.setKeyEntry("feiq",privateKey,new char[0],new java.security.cert.Certificate[]{certificate});
            KeyManagerFactory factory=KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());factory.init(store,new char[0]);keys=factory.getKeyManagers();
            File pins=new File(root,"trusted-devices.properties");if(pins.isFile())try(InputStream input=new FileInputStream(pins)){trusted.load(input);}
            for(String id:trusted.stringPropertyNames())if(Protocol.normalizeDeviceId(id)==null||!validFingerprint(trusted.getProperty(id)))throw new IOException("可信设备记录损坏，未自动重置信任");
        } catch(GeneralSecurityException e){throw new IOException("设备安全身份无法加载",e);}
    }
    public static boolean validFingerprint(String value){return value!=null&&value.matches("[0-9a-f]{64}");}
    public static String hash(byte[] bytes){try{return hex(MessageDigest.getInstance("SHA-256").digest(bytes));}catch(NoSuchAlgorithmException e){throw new IllegalStateException(e);}}
    public static String hashFile(File file)throws IOException{
        try(InputStream input=new FileInputStream(file)){MessageDigest sha=MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))!=-1)sha.update(buffer,0,count);return hex(sha.digest());}
        catch(NoSuchAlgorithmException e){throw new IllegalStateException(e);}
    }
    private static String hex(byte[] bytes){StringBuilder result=new StringBuilder();for(byte b:bytes)result.append(String.format(Locale.ROOT,"%02x",b&255));return result.toString();}
    public static String pairCode(String first,String second){String[] values={first,second};Arrays.sort(values);String hash=hash(("Feiq pairing v1\n"+String.join("\n",values)).getBytes(StandardCharsets.US_ASCII));return (hash.substring(0,4)+" "+hash.substring(4,8)+" "+hash.substring(8,12)+" "+hash.substring(12,16)).toUpperCase(Locale.ROOT);}
    public synchronized String trustedFingerprint(String id){return id==null?null:trusted.getProperty(id);}
    public boolean isTrusted(String id,String value){return validFingerprint(value)&&value.equals(trustedFingerprint(id));}
    public synchronized boolean isTrustedFingerprint(String value){return validFingerprint(value)&&trusted.containsValue(value);}
    public void observeRevocation(java.util.function.Consumer<String> observer){revocations.add(observer);}
    public void removeRevocationObserver(java.util.function.Consumer<String> observer){revocations.remove(observer);}
    public boolean ensureTrusted(String id,String value,String name)throws IOException{
        if(isTrusted(id,value))return true;
        if(!pairingGate.tryLock())throw new TrustException("已有设备正在核对身份，请完成后重试");
        try{return confirmTrust(id,value,name);}finally{pairingGate.unlock();}
    }
    private boolean confirmTrust(String id,String value,String name)throws IOException{
        int version;synchronized(this){version=revision;}
        if(Protocol.normalizeDeviceId(id)==null||!validFingerprint(value))throw new IOException("对方安全身份无效");
        String previous=trustedFingerprint(id);
        if(previous!=null){if(!previous.equals(value))throw new TrustException("对方设备证书已变化，已停止连接。请先核对身份并清除旧信任");return true;}
        if(confirmation==null||!confirmation.confirm(name,pairCode(fingerprint,value)))return false;
        synchronized(this){if(version!=revision)throw new TrustException("配对期间网络或信任已变化，请重新操作");if(trusted.containsKey(id)&&!value.equals(trusted.getProperty(id)))throw new TrustException("配对期间设备身份发生变化");Properties next=new Properties();next.putAll(trusted);next.setProperty(id,value);saveTrusted(next);}
        return true;
    }
    public void forget(String id)throws IOException{String value;synchronized(this){revision++;value=trusted.getProperty(id);Properties next=new Properties();next.putAll(trusted);next.remove(id);saveTrusted(next);}if(value!=null)for(java.util.function.Consumer<String> observer:revocations)observer.accept(value);}
    public synchronized void cancelConfirmations(){revision++;}
    private final Map<Integer,java.util.function.Consumer<Socket>> reverseListeners=new HashMap<>();
    synchronized void registerReverseListener(int port,java.util.function.Consumer<Socket> accept){if(reverseListeners.putIfAbsent(port,accept)!=null)throw new IllegalStateException("端口已注册");}
    synchronized void unregisterReverseListener(int port){reverseListeners.remove(port);}
    boolean acceptReverse(int port,Socket socket){java.util.function.Consumer<Socket> accept;synchronized(this){accept=reverseListeners.get(port);}if(accept==null)return false;accept.accept(socket);return true;}
    private void saveTrusted(Properties next)throws IOException{ByteArrayOutputStream bytes=new ByteArrayOutputStream();next.store(bytes,"Feiq trusted devices");atomic(new File(root,"trusted-devices.properties"),bytes.toByteArray());trusted.clear();trusted.putAll(next);}
    private SSLContext context(String expected)throws GeneralSecurityException{
        TrustManager trust=new X509TrustManager(){
            public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
            private void check(X509Certificate[] chain)throws CertificateException{if(chain==null||chain.length==0)throw new CertificateException("缺少设备证书");chain[0].checkValidity();if(expected!=null&&!expected.equals(hash(chain[0].getEncoded())))throw new CertificateException("设备证书指纹不一致");}
            public void checkClientTrusted(X509Certificate[] chain,String auth)throws CertificateException{check(chain);}
            public void checkServerTrusted(X509Certificate[] chain,String auth)throws CertificateException{check(chain);}
        };
        SSLContext context=SSLContext.getInstance("TLS");context.init(keys,new TrustManager[]{trust},new SecureRandom());return context;
    }
    public SSLSocket client(Socket socket,String expected)throws IOException{
        if(!validFingerprint(expected))throw new IOException("未取得对方设备证书，不能建立加密连接");
        try{SSLSocket tls=(SSLSocket)context(expected).getSocketFactory().createSocket(socket,"Feiq",socket.getPort(),true);tls.setEnabledProtocols(new String[]{"TLSv1.2"});tls.setUseClientMode(true);handshake(tls);return tls;}
        catch(GeneralSecurityException e){throw new IOException("无法建立加密连接",e);}
    }
    public SSLSocket server(Socket socket,boolean requireClient)throws IOException{
        try{SSLSocket tls=(SSLSocket)context(null).getSocketFactory().createSocket(socket,null,socket.getPort(),true);tls.setEnabledProtocols(new String[]{"TLSv1.2"});tls.setUseClientMode(false);tls.setNeedClientAuth(requireClient);handshake(tls);return tls;}
        catch(GeneralSecurityException e){throw new IOException("无法建立加密连接",e);}
    }
    private static void handshake(SSLSocket socket)throws IOException{
        java.util.concurrent.ScheduledFuture<?> timeout=handshakes.schedule(()->{try{socket.close();}catch(IOException ignored){}},5,java.util.concurrent.TimeUnit.SECONDS);
        try{socket.startHandshake();}catch(IOException|RuntimeException error){socket.close();throw error;}finally{timeout.cancel(false);}
    }
    public static String remoteFingerprint(SSLSocket socket)throws IOException{try{return hash(socket.getSession().getPeerCertificates()[0].getEncoded());}catch(CertificateEncodingException e){throw new IOException("证书无效",e);}}
    public static void atomic(File path,byte[] bytes)throws IOException{
        File temporary=new File(path.getPath()+".new");try(FileOutputStream output=new FileOutputStream(temporary)){output.write(bytes);output.getFD().sync();}
        Files.move(temporary.toPath(),path.toPath(),StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE);
    }
    public static void writeFrame(OutputStream output,byte[] bytes)throws IOException{if(bytes.length>4*1024*1024)throw new IOException("安全报文超过 4 MiB");DataOutputStream data=new DataOutputStream(output);data.writeInt(bytes.length);data.write(bytes);data.flush();}
    public static byte[] readFrame(InputStream input,int maximum)throws IOException{DataInputStream data=new DataInputStream(input);int size=data.readInt();if(size<0||size>maximum)throw new IOException("安全报文超过限制");byte[] bytes=new byte[size];data.readFully(bytes);return bytes;}
    public static void writeText(OutputStream output,String text)throws IOException{writeFrame(output,text.getBytes(StandardCharsets.UTF_8));}
    public static String readText(InputStream input,int maximum)throws IOException{return StandardCharsets.UTF_8.newDecoder().decode(java.nio.ByteBuffer.wrap(readFrame(input,maximum))).toString();}

    // Android 公共 API 没有自签证书构造器；这里仅编码 X.509，签名与 TLS 均由系统密码库完成。
    private static X509Certificate createCertificate(KeyPair pair)throws GeneralSecurityException,IOException{
        byte[] algorithm=der(0x30,join(der(6,new byte[]{42,(byte)134,72,(byte)134,(byte)247,13,1,1,11}),der(5,new byte[0])));
        byte[] name=der(0x30,der(0x31,der(0x30,join(der(6,new byte[]{85,4,3}),der(12,"Feiq".getBytes(StandardCharsets.UTF_8))))));
        SimpleDateFormat format=new SimpleDateFormat("yyyyMMddHHmmss'Z'",Locale.ROOT);format.setTimeZone(TimeZone.getTimeZone("UTC"));
        long now=System.currentTimeMillis();byte[] dates=der(0x30,join(der(24,format.format(new Date(now-86400000L)).getBytes(StandardCharsets.US_ASCII)),der(24,format.format(new Date(now+20L*365*86400000L)).getBytes(StandardCharsets.US_ASCII))));
        byte[] serial=new BigInteger(120,new SecureRandom()).add(BigInteger.ONE).toByteArray();
        byte[] body=der(0x30,join(der(2,serial),algorithm,name,dates,name,pair.getPublic().getEncoded()));
        Signature sign=Signature.getInstance("SHA256withRSA");sign.initSign(pair.getPrivate());sign.update(body);
        byte[] encoded=der(0x30,join(body,algorithm,der(3,join(new byte[]{0},sign.sign()))));
        return (X509Certificate)CertificateFactory.getInstance("X.509").generateCertificate(new ByteArrayInputStream(encoded));
    }
    private static byte[] der(int tag,byte[] bytes)throws IOException{ByteArrayOutputStream out=new ByteArrayOutputStream();out.write(tag);if(bytes.length<128)out.write(bytes.length);else{int size=bytes.length>65535?3:bytes.length>255?2:1;out.write(128|size);for(int i=size-1;i>=0;i--)out.write(bytes.length>>(8*i));}out.write(bytes);return out.toByteArray();}
    private static byte[] join(byte[]... arrays)throws IOException{ByteArrayOutputStream out=new ByteArrayOutputStream();for(byte[] bytes:arrays)out.write(bytes);return out.toByteArray();}
}
