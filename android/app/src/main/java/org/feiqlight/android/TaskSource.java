package org.feiqlight.android;

final class TaskSource {
    final String key,name,avatar;
    final long time;
    TaskSource(String key,String name,String avatar,long time) {
        this.key=key;
        this.name=name;
        this.avatar=avatar;
        this.time=time;
    }
}
