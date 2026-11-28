using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FeiqLight
{
    public sealed partial class LanService
    {
        private void SaveOffers(Dictionary<string,Offer> snapshot = null)
        {
            if(dataRoot==null)return;
            lock(gate) {
                if(disposed)return;
                // 同一批的消息只保存一次，避免附件数量增长时重复写入整批记录。
                HashSet<long> records = new HashSet<long>();
                var saved=(snapshot??offers).Select(o=>new StoredOffer{
                    Key=o.Key,Path=o.Value.Path,DeviceId=o.Value.DeviceId,Fingerprint=o.Value.Fingerprint,Sha256=o.Value.Sha256,Size=o.Value.Size,
                        ModifiedTicks=o.Value.Modified.Ticks,ExpiresTicks=o.Value.Expires.Ticks,FileId=o.Value.FileId,
                        Record=records.Add(o.Value.Record.Packet)?o.Value.Record:null,TransferState=o.Value.TransferState,Percent=o.Value.Percent
                }).ToArray();
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=OfferJsonLimit}.Serialize(saved));
                if(bytes.Length>OfferJsonLimit)throw new IOException("文件邀请记录过大，请先处理待发送文件。");
                string path=Path.Combine(dataRoot,"transfer-offers.json");
                for(int attempt=0;;attempt++){
                    try{SecureIdentity.AtomicBytes(path,bytes);break;}
                    catch(IOException error){
                        int code=error.HResult&0xffff;
                        // 连续原子替换可能遇到扫描器的短暂文件占用；只重试明确的共享/替换占用错误。
                        if(attempt>=3||(code!=32&&code!=33&&code!=1175))throw;
                        Thread.Sleep(25*(attempt+1));
                    }
                }
            }
        }
        private void RestoreOffers()
        {
            string path=Path.Combine(dataRoot,"transfer-offers.json");if(!File.Exists(path))return;
            if(new FileInfo(path).Length>OfferJsonLimit)throw new IOException("文件任务记录过大。");
            var saved=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=OfferJsonLimit}.Deserialize<StoredOffer[]>(File.ReadAllText(path,System.Text.Encoding.UTF8));
            if(saved==null||saved.Length>MaxStoredOffers)throw new IOException("文件任务记录损坏。");
            var records=saved.Where(item=>item!=null&&item.Record!=null).GroupBy(item=>item.Record.Packet).ToDictionary(group=>group.Key,group=>group.First().Record);
            foreach(StoredOffer item in saved) {
                if(item!=null&&item.Record==null){
                    long packet;
                    string[] key=(item.Key??"").Split(':');
                    ChatRecord record;
                    if(key.Length==2&&Int64.TryParse(key[0],out packet)&&records.TryGetValue(packet,out record))item.Record=record;
                }
                if(item==null||item.Record==null||item.ExpiresTicks<=DateTime.UtcNow.Ticks||!Security.IsTrusted(item.DeviceId,item.Fingerprint)||!SecureIdentity.ValidFingerprint(item.Sha256))continue;
                FileInfo file=new FileInfo(item.Path);if(!file.Exists||file.Length!=item.Size||file.LastWriteTimeUtc.Ticks!=item.ModifiedTicks)continue;
                if(item.Key!=item.Record.Packet+":"+item.FileId)throw new IOException("文件任务编号无效。");
                FileTransferState previous=(item.Record.Transfers??new FileTransferState[0]).FirstOrDefault(transfer=>transfer.Id==item.FileId);
                string transferState=item.TransferState??(previous==null?null:previous.Status);
                int percent=item.TransferState==null?(previous==null?0:previous.Percent):item.Percent;
                offers[item.Key]=new Offer{
                    Path=file.FullName,Size=item.Size,Modified=file.LastWriteTimeUtc,Expires=new DateTime(item.ExpiresTicks,DateTimeKind.Utc),
                        DeviceId=item.DeviceId,Fingerprint=item.Fingerprint,Sha256=item.Sha256,Record=item.Record,FileId=item.FileId,TransferState=transferState,
                        Percent=Math.Max(0,Math.Min(100,percent))
                };
                sequence=Math.Max(sequence,item.Record.Packet);
            }
        }

        public ChatRecord OutgoingRecord(string peer, long packet) {
            lock(gate) {
                Offer offer=offers.Values.FirstOrDefault(o=>!o.Canceled&&o.Record.PeerId==peer&&o.Record.Packet==packet&&o.Expires>DateTime.UtcNow);
                return offer==null?null:offer.Record;
            }
        }

        private static bool IsOfferCompleted(Offer offer) { return offer!=null&&(offer.TransferState=="已发送（非保存确认）"||offer.TransferState=="对方已校验保存"); }
        private static bool IsUnconfirmedState(string state) { string s=state??"";return s=="等待确认"||s.Contains("未确认")||s.Contains("失败"); }

        private bool TryBeginOfferStream(string key,Func<Offer,bool> authorize,TcpClient client,out Offer offer,out long version)
        {
            lock(gate) {
                if(!disposed&&offers.TryGetValue(key,out offer)&&!offer.Canceled&&offer.Expires>=DateTime.UtcNow&&authorize(offer)) {
                    version=++offer.ProgressVersion;offer.ActiveClients.Add(client);return true;
                }
                offer=null;version=0;return false;
            }
        }
        private void EndOfferStream(Offer offer,TcpClient client) { if(offer!=null&&client!=null)lock(gate)offer.ActiveClients.Remove(client); }

        public bool HasActiveOutgoingTransfer(string peerId,long packet) {
            lock(gate)return offers.Any(item=>item.Value.Record.PeerId==peerId&&item.Value.Record.Packet==packet&&!item.Value.Canceled&&item.Value.ActiveClients.Count>0);
        }
        public bool HasActiveOutgoingTransfer(string peerId) { lock(gate)return offers.Any(item=>item.Value.Record.PeerId==peerId&&!item.Value.Canceled&&item.Value.ActiveClients.Count>0); }
        public bool CanCancelOutgoing(string peerId,long packet)
        {
            lock(gate) return pending.Any(p=>p.Key==packet&&!p.Value.Canceled&&!p.Value.Delivered&&p.Value.Record.PeerId==peerId)
                ||offers.Any(o=>o.Value.Record.PeerId==peerId&&o.Value.Record.Packet==packet&&!o.Value.Canceled&&o.Value.Expires>=DateTime.UtcNow&&!IsOfferCompleted(o.Value));
        }
        public bool HasUndeliveredOutgoing(string peerId,long packet,string recordState)
        {
            lock(gate) return pending.Any(p=>p.Key==packet&&!p.Value.Canceled&&!p.Value.Delivered&&p.Value.Record.PeerId==peerId)||(IsUnconfirmedState(recordState)
                &&offers.Any(o=>o.Value.Record.PeerId==peerId&&o.Value.Record.Packet==packet&&!o.Value.Canceled&&!IsOfferCompleted(o.Value)));
        }

        private void CheckOfferCapacity(int count,IEnumerable<string> replaced)
        {
            if(count==0)return;
            foreach(string key in offers.Where(item=>item.Value.Expires<DateTime.UtcNow&&item.Value.ActiveClients.Count==0).Select(item=>item.Key).ToList())offers.Remove(key);
            // 完成邀请只作重新接收缓存；不计待处理额度，压力下淘汰最旧项。
            var completed=offers.Where(item=>IsOfferCompleted(item.Value)&&item.Value.ActiveClients.Count==0).OrderByDescending(item=>item.Value.Expires).ToList();
            foreach(var item in completed.Skip(CompletedOfferCache))offers.Remove(item.Key);
            HashSet<string> replacement=new HashSet<string>(replaced??Enumerable.Empty<string>());
            int waiting=offers.Count(item=>!IsOfferCompleted(item.Value)&&!replacement.Contains(item.Key));
            if(waiting+count>Settings.PendingFileLimit)throw new InvalidOperationException("待处理文件已达设置上限 "+Settings.PendingFileLimit+"，请处理已有任务或在设置中调大。");
        }

        private void Transfer(Offer offer, long version, int percent, string status, double bytesPerSecond = 0)
        {
            lock(gate) {
                if(version != offer.ProgressVersion) return; offer.Percent=percent; offer.TransferState=status;
                var handler = TransferChanged; if (!disposed && handler != null) handler(offer.Record, offer.FileId, percent, status, bytesPerSecond);
            }
        }
        private void EndOffer(Offer offer, string state) { if(offer.TransferState != "已发送（非保存确认）"&&offer.TransferState!="对方已校验保存") Transfer(offer, ++offer.ProgressVersion, offer.Percent, state); }

        private void StreamOffer(TcpClient client,SslStream stream,string key,Func<Offer,bool> authorize,long offset)
        {
            Offer offer;long version;
            if (!TryBeginOfferStream(key, authorize, client, out offer, out version))
            {
                bool revoked;
                lock (gate)
                {
                    Offer current;
                    revoked = !disposed && (!offers.TryGetValue(key, out current) || (current.Canceled || current.Expires < DateTime.UtcNow) && authorize(current));
                }
                if (revoked) SecureIdentity.WriteText(stream, "REVOKED");
                throw new IOException("文件授权无效或已取消。");
            }
            try {
                FileInfo current=new FileInfo(offer.Path);if(!current.Exists||current.Length!=offer.Size||current.LastWriteTimeUtc!=offer.Modified)throw new IOException("源文件已改变。");
                SecureIdentity.WriteText(stream,"OK\n"+offer.Size+"\n"+offer.Sha256);
                long sent=offset;TransferRate rate=new TransferRate(offset);var timing=System.Diagnostics.Stopwatch.StartNew();
                using(FileStream file=File.OpenRead(offer.Path)) {
                    file.Position=offset;
                    byte[] buffer=new byte[65536];
                    while(sent<offer.Size){
                        int count=file.Read(buffer,0,(int)Math.Min(buffer.Length,offer.Size-sent));
                        if(count==0)throw new IOException("源文件读取中断，请重新发送文件。");
                        stream.Write(buffer,0,count);
                        sent+=count;
                        lock(gate)offer.Expires=DateTime.UtcNow.AddDays(7);
                        if(rate.Update(sent,timing.Elapsed.TotalMilliseconds))Transfer(offer,version,(int)(sent*100.0/Math.Max(1,offer.Size)),"发送中",rate.BytesPerSecond);
                    }
                }
                stream.Flush();Transfer(offer,version,100,"已发送，等待校验保存");
                if(SecureIdentity.ReadText(stream,128)=="STORED\n"+offer.Sha256){Transfer(offer,version,100,"对方已校验保存");try{SaveOffers();}catch(IOException error){Report("文件完成状态保存失败："+error.Message);}}
            }catch(IOException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            catch(ObjectDisposedException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            finally {EndOfferStream(offer,client);}
        }
    }
}
