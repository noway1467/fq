package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.util.concurrent.TimeUnit;

/** 整条请求共用单调时钟期限，不能通过滴入字节不断延长未认证连接。 */
final class RequestInput extends FilterInputStream {
    private final Socket socket;
    private long deadline;
    RequestInput(Socket socket)throws IOException {super(socket.getInputStream());this.socket=socket;}
    void begin(int milliseconds){deadline=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(milliseconds);}
    private void beforeRead()throws IOException {
        long remaining=TimeUnit.NANOSECONDS.toMillis(deadline-System.nanoTime());
        if(remaining<=0)throw new SocketTimeoutException("请求读取超时");
        socket.setSoTimeout((int)remaining);
    }
    @Override public int read()throws IOException {beforeRead();return in.read();}
    @Override public int read(byte[] buffer,int offset,int count)throws IOException {beforeRead();return in.read(buffer,offset,count);}
}
