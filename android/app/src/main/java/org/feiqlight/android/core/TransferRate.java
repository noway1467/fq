package org.feiqlight.android.core;

/** 每次连接单独计时；已有断点只作基线，不计入本次速率。 */
public final class TransferRate {
    private long previousBytes;
    private double previousTime;
    private double bytesPerSecond;
    public TransferRate(long offset) { previousBytes=offset; }
    public double bytesPerSecond() { return bytesPerSecond; }
    public boolean update(long bytes,double milliseconds) {
        double elapsed=milliseconds-previousTime;
        if(elapsed<250) return false;
        bytesPerSecond=(bytes-previousBytes)*1000.0/elapsed;
        previousBytes=bytes;previousTime=milliseconds;return true;
    }
}
