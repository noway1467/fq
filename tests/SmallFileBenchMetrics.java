package org.feiqlight.android.core;

import java.io.*;
import java.lang.management.ManagementFactory;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.*;

/** 仅在基准临时编译目录注入，APK 不包含计时代码。 */
public final class SmallFileBenchMetrics {
    private static final Map<String,Double> segments=new LinkedHashMap<>();
    private static final Map<String,Integer> counts=new LinkedHashMap<>();
    private static final Set<String> stored=new HashSet<>();
    private static boolean enabled;
    private static long cpuStart;
    private SmallFileBenchMetrics() { }
    public static long stamp() { return System.nanoTime(); }
    public static synchronized int storedCount() {return stored.size();}
    public static synchronized void stored(String key) {if(enabled)stored.add(key);}
    public static synchronized void add(String name,long started) {
        if(!enabled)return;
        segments.merge(name,(stamp()-started)/1000000.0,Double::sum);
        counts.merge(name,1,Integer::sum);
    }
    public static synchronized void start() {
        segments.clear();counts.clear();stored.clear();cpuStart=cpu();enabled=true;
    }
    private static long cpu() {
        return ((com.sun.management.OperatingSystemMXBean)ManagementFactory.getOperatingSystemMXBean()).getProcessCpuTime();
    }
    public static synchronized void write(File path,double elapsed,List<Double> latencies)throws IOException {
        enabled=false;
        StringBuilder json=new StringBuilder("{\"segmentsMs\":{");
        boolean separator=false;
        for(Map.Entry<String,Double> entry:segments.entrySet()) {
            if(separator)json.append(',');separator=true;
            json.append('"').append(entry.getKey()).append("\":").append(entry.getValue());
        }
        json.append("},\"counts\":{");separator=false;
        for(Map.Entry<String,Integer> entry:counts.entrySet()) {
            if(separator)json.append(',');separator=true;
            json.append('"').append(entry.getKey()).append("\":").append(entry.getValue());
        }
        json.append("},\"storedFiles\":").append(stored.size()).append(",\"elapsedMs\":").append(elapsed).append(",\"latenciesMs\":").append(latencies);
        json.append(",\"cpuMs\":").append((cpu()-cpuStart)/1000000.0);
        json.append(",\"managedBytes\":").append(Runtime.getRuntime().totalMemory()-Runtime.getRuntime().freeMemory()).append('}');
        Files.write(path.toPath(),json.toString().getBytes(StandardCharsets.UTF_8));
    }
}
