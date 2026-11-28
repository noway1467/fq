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
        private const int CompletedOfferFlushCount = 32, CompletedOfferFlushDelay = 250, CompletionJournalLimit = 256 * 1024;
        private int unflushedOfferCompletions;
        private bool completionJournalCleanupPending;
        private Timer offerFlushTimer;
        private sealed class StoredCompletion
        {
            internal string Sha256, DeviceId, Fingerprint;
            internal long Expires;
        }

        private void FlushOfferCompletions()
        {
            lock (gate)
            {
                if (unflushedOfferCompletions == 0 && !completionJournalCleanupPending || disposed) return;
                try { SaveOffers(); }
                catch (IOException error) { Report("文件完成快照保存失败，确认日志已保留：" + error.Message); }
                catch (UnauthorizedAccessException error) { Report("文件完成快照保存失败，确认日志已保留：" + error.Message); }
            }
        }

        private void StoreOfferCompletion(Offer offer)
        {
            lock (gate)
            {
                Offer current;
                if (disposed || offer.Canceled || !offers.TryGetValue(offer.Record.Packet+":"+offer.FileId,out current) || current!=offer ||
                    !Security.IsTrusted(offer.DeviceId, offer.Fingerprint)) throw new IOException("文件授权已结束，保存确认未提交。");
                if (!offer.Stored)
                {
                    string journal = Path.Combine(dataRoot, "transfer-offer-completions.log");
                    byte[] entry = System.Text.Encoding.ASCII.GetBytes(offer.Record.Packet.ToString(CultureInfo.InvariantCulture) + ":" +
                        offer.FileId.ToString(CultureInfo.InvariantCulture) + ":" + offer.Expires.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + offer.Sha256 + ":" +
                        offer.DeviceId + ":" + offer.Fingerprint + "\n");
                    if (File.Exists(journal) && new FileInfo(journal).Length + entry.Length > CompletionJournalLimit) SaveOffers();
                    if (File.Exists(journal) && new FileInfo(journal).Length + entry.Length > CompletionJournalLimit)
                        throw new IOException("文件完成确认日志暂不能清理，请稍后重试。");
                    // 每件先同步确认日志；进程崩溃时，尚未合入快照的完成项仍不会重占待处理额度。
                    using (FileStream output = new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                    {
                        long end=output.Length;
                        if(end>0) {
                            output.Position=end-1;
                            if(output.ReadByte()!='\n') {
                                // 同一进程上次写入失败也可能留下半条，不能把下一条接到坏尾部上。
                                while(end>0){output.Position=--end;if(output.ReadByte()=='\n'){end++;break;}}
                                output.SetLength(end);
                            }
                        }
                        output.Position=output.Length;
                        output.Write(entry, 0, entry.Length); output.Flush(true);
                    }
                    offer.Stored = true;
                    unflushedOfferCompletions++;
                    if (offerFlushTimer == null) offerFlushTimer = new Timer(_ => FlushOfferCompletions(), null, Timeout.Infinite, Timeout.Infinite);
                    if (unflushedOfferCompletions == 1) offerFlushTimer.Change(CompletedOfferFlushDelay, Timeout.Infinite);
                }
                Transfer(offer, offer.ProgressVersion, 100, "对方已校验保存");
                if (unflushedOfferCompletions >= CompletedOfferFlushCount || offers.Values.All(IsOfferCompleted)) FlushOfferCompletions();
            }
        }

        private Dictionary<string,StoredCompletion> ReadOfferCompletions()
        {
            string journal = Path.Combine(dataRoot, "transfer-offer-completions.log");
            var completed = new Dictionary<string,StoredCompletion>();
            if (!File.Exists(journal) || new FileInfo(journal).Length == 0) return completed;
            if (new FileInfo(journal).Length > CompletionJournalLimit) throw new IOException("文件完成确认日志过大。");
            byte[] bytes = File.ReadAllBytes(journal);
            int complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            string text = System.Text.Encoding.ASCII.GetString(bytes, 0, complete);
            string[] lines=text.Split('\n');
            foreach (string line in lines.Take(lines.Length-1))
            {
                string[] fields = line.Split(':'); long packet, expires; int file;
                if (fields.Length != 6 || !Int64.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out packet) || packet < 0 ||
                    !Int32.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out file) || file < 0 ||
                    !Int64.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out expires) || expires > DateTime.MaxValue.Ticks ||
                    !SecureIdentity.ValidFingerprint(fields[3]) || Protocol.NormalizeDeviceId(fields[4])==null || !SecureIdentity.ValidFingerprint(fields[5]))
                    throw new IOException("文件完成确认日志损坏。");
                completed[packet+":"+file] = new StoredCompletion { Sha256=fields[3], Expires=expires, DeviceId=Protocol.NormalizeDeviceId(fields[4]), Fingerprint=fields[5] };
            }
            return completed;
        }

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
                        Record=records.Add(o.Value.Record.Packet)?o.Value.Record:null,
                        TransferState=o.Value.Stored?"对方已校验保存":o.Value.TransferState,Percent=o.Value.Stored?100:o.Value.Percent
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
                string journal=Path.Combine(dataRoot,"transfer-offer-completions.log");
                // 原子替换已是业务提交点；清理失败只能保留幂等日志，不能让调用方回滚内存授权。
                completionJournalCleanupPending=false;
                try {
                    if(File.Exists(journal)&&new FileInfo(journal).Length>0)
                        using(FileStream output=new FileStream(journal,FileMode.Create,FileAccess.Write,FileShare.Read))output.Flush(true);
                }
                catch(IOException error){completionJournalCleanupPending=true;Report("文件快照已保存，确认日志待下次清理："+error.Message);}
                catch(UnauthorizedAccessException error){completionJournalCleanupPending=true;Report("文件快照已保存，确认日志待下次清理："+error.Message);}
                unflushedOfferCompletions=0;
                if(offerFlushTimer!=null)offerFlushTimer.Change(Timeout.Infinite,Timeout.Infinite);
            }
        }
        private void RestoreOffers()
        {
            string path=Path.Combine(dataRoot,"transfer-offers.json");if(!File.Exists(path))return;
            if(new FileInfo(path).Length>OfferJsonLimit)throw new IOException("文件任务记录过大。");
            var saved=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=OfferJsonLimit}.Deserialize<StoredOffer[]>(File.ReadAllText(path,System.Text.Encoding.UTF8));
            if(saved==null||saved.Length>MaxStoredOffers)throw new IOException("文件任务记录损坏。");
            Dictionary<string,StoredCompletion> completed=ReadOfferCompletions();
            var records=saved.Where(item=>item!=null&&item.Record!=null).GroupBy(item=>item.Record.Packet).ToDictionary(group=>group.Key,group=>group.First().Record);
            foreach(StoredOffer item in saved) {
                if(item!=null&&item.Record==null){
                    long packet;
                    string[] key=(item.Key??"").Split(':');
                    ChatRecord record;
                    if(key.Length==2&&Int64.TryParse(key[0],out packet)&&records.TryGetValue(packet,out record))item.Record=record;
                }
                if(item==null||item.Record==null)continue;
                StoredCompletion completion;
                bool stored=completed.TryGetValue(item.Key??"",out completion)&&completion.Sha256==item.Sha256&&
                    completion.DeviceId==item.DeviceId&&completion.Fingerprint==item.Fingerprint;
                long expires=stored?Math.Max(item.ExpiresTicks,completion.Expires):item.ExpiresTicks;
                // 下载会延长邀请有效期，先合入确认日志再判断过期，避免丢掉跨过原截止时刻的完成项。
                if(expires<=DateTime.UtcNow.Ticks||!Security.IsTrusted(item.DeviceId,item.Fingerprint)||!SecureIdentity.ValidFingerprint(item.Sha256))continue;
                FileInfo file=new FileInfo(item.Path);if(!file.Exists||file.Length!=item.Size||file.LastWriteTimeUtc.Ticks!=item.ModifiedTicks)continue;
                if(item.Key!=item.Record.Packet+":"+item.FileId)throw new IOException("文件任务编号无效。");
                FileTransferState previous=(item.Record.Transfers??new FileTransferState[0]).FirstOrDefault(transfer=>transfer.Id==item.FileId);
                string transferState=item.TransferState??(previous==null?null:previous.Status);
                int percent=item.TransferState==null?(previous==null?0:previous.Percent):item.Percent;
                if(stored){transferState="对方已校验保存";percent=100;}
                if(previous!=null&&transferState=="对方已校验保存") {
                    previous.Status=transferState;previous.Percent=100;previous.BytesPerSecond=0;previous.MarkCurrent();
                }
                offers[item.Key]=new Offer{
                    Path=file.FullName,Size=item.Size,Modified=file.LastWriteTimeUtc,Expires=new DateTime(expires,DateTimeKind.Utc),
                        DeviceId=item.DeviceId,Fingerprint=item.Fingerprint,Sha256=item.Sha256,Record=item.Record,FileId=item.FileId,TransferState=transferState,
                        Percent=Math.Max(0,Math.Min(100,percent)),Stored=transferState=="对方已校验保存"
                };
                sequence=Math.Max(sequence,item.Record.Packet);
            }
            string journal=Path.Combine(dataRoot,"transfer-offer-completions.log");
            // 只忽略崩溃截断的最后半条；完整但损坏的记录必须报错。先写新快照，再清日志。
            if(File.Exists(journal)&&new FileInfo(journal).Length>0)SaveOffers();
        }

        public ChatRecord OutgoingRecord(string peer, long packet) {
            lock(gate) {
                Offer offer=offers.Values.FirstOrDefault(o=>!o.Canceled&&o.Record.PeerId==peer&&o.Record.Packet==packet&&o.Expires>DateTime.UtcNow);
                return offer==null?null:offer.Record;
            }
        }

        public ChatRecord[] CompletedOutgoingRecords() {
            lock(gate)return offers.Values.Where(offer=>offer.Stored).Select(offer=>offer.Record).Distinct().ToArray();
        }

        private static bool IsOfferCompleted(Offer offer) { return offer!=null&&(offer.Stored||offer.TransferState=="已发送（非保存确认）"||offer.TransferState=="对方已校验保存"); }
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
                if(version != offer.ProgressVersion || IsOfferCompleted(offer) && status != "对方已校验保存") return; offer.Percent=percent; offer.TransferState=status;
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
                if(SecureIdentity.ReadText(stream,128)=="STORED\n"+offer.Sha256){
                    try{StoreOfferCompletion(offer);}
                    catch(IOException error){Report("文件完成状态保存失败："+error.Message);throw;}
                    catch(UnauthorizedAccessException error){Report("文件完成状态保存失败："+error.Message);throw new IOException("文件完成确认无法落盘。",error);}
                } else throw new IOException("文件保存确认边界无效。");
            }catch(IOException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            catch(ObjectDisposedException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            finally {EndOfferStream(offer,client);}
        }
    }
}
