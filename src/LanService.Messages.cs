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
        private void NotifyDelivery(ChatRecord record) { Action<ChatRecord> handler=DeliveryChanged;if(!disposed&&handler!=null)handler(record); }

        private long Next() { return Interlocked.Increment(ref sequence); }
        private byte[] Encode(uint command, string body, string extra) {
            uint mode = command & 255;
            return Protocol.Encode(Next(), Login, Host, command | Protocol.Utf8, body, extra, Settings.DeviceId,
                mode == Protocol.Entry || mode == Protocol.AnswerEntry || mode == Protocol.Absence ? Settings.Avatar ?? "default" : null,
                Security==null ? null : Security.Fingerprint);
        }

        private void ReceiveLoop()
        {
            while (!disposed)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = udp.Receive(ref remote); Packet packet;
                    if(!LoopbackOnly&&LanNetwork.Route(links,remote.Address)==null)continue;
                    if (!Protocol.TryParse(bytes, out packet)) continue;
                    if (IsSelf(packet, remote)) continue;
                    Handle(packet, packet.ReplyPort>0?new IPEndPoint(remote.Address,packet.ReplyPort):remote);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e) { if (!disposed) { Report("网络接收错误：" + e.Message); Thread.Sleep(100); } }
                catch (Exception e) { if (!disposed) Report("消息处理错误：" + e.Message); }
            }
        }
        private void Handle(Packet packet, IPEndPoint remote, bool secure=false)
        {
            bool discovery = packet.Mode == Protocol.Entry || packet.Mode == Protocol.AnswerEntry || packet.Mode == Protocol.Absence || packet.Mode == Protocol.Exit;
            if (!discovery && !(secure && (packet.Mode == Protocol.SendMessage || packet.Mode == Protocol.ReleaseFiles))) return;
            if (packet.Mode == Protocol.ReleaseFiles)
            {
                long messageId;
                if ((packet.Body ?? "").StartsWith("cancel:", StringComparison.Ordinal))
                {
                    if (!Int64.TryParse(packet.Body.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out messageId) || messageId <= 0) return;
                    Peer sender;
                    lock (gate)
                    {
                        if (!Security.IsTrusted(packet.DeviceId, packet.Fingerprint)) return;
                        sender = peers.Values.FirstOrDefault(p => p.DeviceId == packet.DeviceId && p.Fingerprint == packet.Fingerprint);
                    }
                    Action<Peer, long> canceled = OfferCanceled;
                    if (sender != null && canceled != null) canceled(sender, messageId);
                    return;
                }
                if (!Int64.TryParse(packet.Body, out messageId)) return;
                lock (gate)
                    foreach (string key in offers.Where(p => p.Key.StartsWith(messageId + ":", StringComparison.Ordinal) && p.Value.DeviceId==packet.DeviceId
                        &&p.Value.Fingerprint==packet.Fingerprint).Select(p => p.Key).ToList()) {
                        EndOffer(offers[key], "对方已拒绝");
                        offers.Remove(key);
                    }
                SaveOffers();
                return;
            }
            Peer peer = Upsert(packet, remote); if (peer == null) return;
            if (packet.Mode == Protocol.Entry)
            {
                uint flags = Protocol.AnswerEntry | Protocol.CapUtf8 | Protocol.FileAttach | (peer.Utf8 ? Protocol.Utf8 : 0);
                try { Send(Encode(flags, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group)), remote); } catch (SocketException) { }
                if (Security != null && peer.DeviceId != null && peer.Fingerprint != null && Security.IsTrusted(peer.DeviceId, peer.Fingerprint)) ScheduleSync(peer);
            }
            if (packet.Mode != Protocol.SendMessage) return;
            lock (gate)
            {
                string key = peer.Id + ":" + packet.Number;
                if (seen.ContainsKey(key)) return;
                if (seen.Count >= 8192) seen.Remove(seen.OrderBy(p => p.Value).First().Key);
                seen[key] = DateTime.UtcNow;
            }
            IncomingMessage message = new IncomingMessage {
                Peer = peer, Record = new ChatRecord {
                    Packet = packet.Number, PeerId = peer.Id, Sender = peer.Nickname, Text = packet.Body, Time = DateTime.Now, Outgoing = false, State = "已收到"
                }
            };
            if ((packet.Command & Protocol.FileAttach) != 0) message.Files = Protocol.ParseFiles(packet.Extra);
            Action<IncomingMessage> handler = MessageReceived; if (handler != null) handler(message);
        }
        private void Complete(ChatRecord record, string state)
        {
            lock (gate)
            {
                // 取消是本机的终态，晚到的切网或发送失败回调不能重新开启重发入口。
                if (record.Outgoing && record.State == "已取消发送" && state != "已取消发送") return;
                record.State = state;
            }
            NotifyDelivery(record);
        }
        private bool CancelOutgoingCore(string peerId,long packet,string knownState,bool notify,bool onlyUndelivered)
        {
            Pending item=null;
            List<KeyValuePair<string,Offer>> selected;
            List<TcpClient> socketsToClose=new List<TcpClient>();
            List<Tuple<Offer,long,int>> notifiedTransfers=new List<Tuple<Offer,long,int>>();
            TcpClient connectingToClose=null;
            ChatRecord stateRecord=null;
            bool wasPending=false,updateRecordState=false;
            Peer cancelPeer = null;
            lock(gate) {
                Pending candidate;
                if(pending.TryGetValue(packet,out candidate)&&candidate.Record.PeerId==peerId&&!candidate.Canceled&&!candidate.Delivered)item=candidate;
                selected=offers.Where(o=>o.Value.Record.PeerId==peerId&&o.Value.Record.Packet==packet&&!o.Value.Canceled&&!IsOfferCompleted(o.Value)).ToList();
                bool active=selected.Any(o=>o.Value.ActiveClients.Count>0);
                string offerState=selected.Select(o=>o.Value.Record.State).FirstOrDefault();
                string effectiveState=item!=null?item.Record.State:offerState=="已送达"?offerState:(!String.IsNullOrEmpty(knownState)?knownState:offerState);
                if(onlyUndelivered&&knownState=="已送达")return false;
                if(onlyUndelivered&&item==null&&(!IsUnconfirmedState(effectiveState)||active))return false;
                if(item==null&&selected.Count==0)return false;
                if (notify && !onlyUndelivered && selected.Count > 0 && (active || effectiveState == "已送达" || selected.Any(o => o.Value.Percent > 0) || item != null && item.Transmitting != 0))
                {
                    Peer target = item != null ? item.Peer : peers.Values.FirstOrDefault(p => p.Id == peerId);
                    if (target != null) {
                        RequireSecurePeer(target.DeviceId, target.Fingerprint);
                        cancelPeer = new Peer {
                            Endpoint = new IPEndPoint(target.Endpoint.Address, target.Endpoint.Port), DeviceId = target.DeviceId,
                            Fingerprint = target.Fingerprint, Login = target.Login, Nickname = target.DisplayName
                        };
                    }
                }
                Dictionary<string,Offer> nextOffers=new Dictionary<string,Offer>(offers);
                foreach(KeyValuePair<string,Offer> entry in selected)nextOffers.Remove(entry.Key);
                if(selected.Count!=0)SaveOffers(nextOffers);
                if(selected.Count!=0)CloseFileBatches(selected[0].Value.DeviceId);
                if(item!=null) {
                    wasPending=true;item.Canceled=true;pending.Remove(packet);connectingToClose=item.ConnectingClient;item.ConnectingClient=null;stateRecord=item.Record;
                }
                foreach(KeyValuePair<string,Offer> entry in selected) {
                    Offer offer=entry.Value;offer.Canceled=true;offers.Remove(entry.Key);socketsToClose.AddRange(offer.ActiveClients);offer.ActiveClients.Clear();
                    long version=++offer.ProgressVersion;offer.TransferState="已取消发送";
                    if(notify)notifiedTransfers.Add(Tuple.Create(offer,version,offer.Percent));
                    if(stateRecord==null)stateRecord=offer.Record;
                }
                updateRecordState=wasPending||(IsUnconfirmedState(effectiveState)&&effectiveState!="已送达");
                if(updateRecordState&&stateRecord!=null)stateRecord.State="已取消发送";
            }
            if(connectingToClose!=null)try{connectingToClose.Close();}catch{}
            foreach(TcpClient client in socketsToClose)try{client.Close();}catch{}
            foreach(Tuple<Offer,long,int> entry in notifiedTransfers)Transfer(entry.Item1,entry.Item2,entry.Item3,"已取消发送");
            if(notify&&updateRecordState&&stateRecord!=null)NotifyDelivery(stateRecord);
            if (cancelPeer != null)
            {
                Peer target = cancelPeer;
                Task.Run(() => { try { SendReleaseFiles(target, "cancel:" + packet.ToString(CultureInfo.InvariantCulture)); } catch (Exception) { /* 拉取的 REVOKED 终态兜底通知丢失。 */ } });
            }
            return true;
        }
        public bool CancelOutgoing(string peerId,long packet,string knownState=null,bool notify=true) { return CancelOutgoingCore(peerId,packet,knownState,notify,false); }
        public bool CancelUndeliveredOutgoing(string peerId,long packet,string knownState,bool notify=false) { return CancelOutgoingCore(peerId,packet,knownState,notify,true); }
        public void CancelUndeliveredOutgoingForPeer(string peerId)
        {
            List<KeyValuePair<long,string>> messages;
            lock(gate) messages=pending.Values.Where(p=>p.Record.PeerId==peerId&&!p.Canceled
                &&!p.Delivered).Select(p=>new KeyValuePair<long,string>(p.Record.Packet,p.Record.State)).Concat(offers.Values.Where(o=>o.Record.PeerId==peerId&&!o.Canceled
                &&IsUnconfirmedState(o.Record.State)).Select(o=>new KeyValuePair<long,string>(o.Record.Packet,
                o.Record.State))).GroupBy(p=>p.Key).Select(g=>g.First()).ToList();
            foreach(KeyValuePair<long,string> message in messages)CancelUndeliveredOutgoing(peerId,message.Key,message.Value,false);
        }
        public ChatRecord SendContent(Peer peer, string text, IList<string> paths, string dataRoot)
        {
            return SendContent(peer, text, paths, dataRoot, null);
        }
        private ChatRecord SendContent(Peer peer, string text, IList<string> paths, string dataRoot, Dictionary<string,string> relatives)
        {
            if (!MessageText.AsFile(text)) return SendMessage(peer, text, paths, null, relatives);
            RequireSecurePeer(peer.DeviceId,peer.Fingerprint);
            if (text.Length > MessageText.InputLimit || text.IndexOf('\0') >= 0) throw new ArgumentException("正文超过 1,048,576 个字符或含有空字符，请直接选择文件发送。");
            if (paths != null && paths.Count >= Protocol.MaxAttachmentsPerMessage) throw new ArgumentException("超长正文需要一个 TXT 附件位置，请最多选择 19 个其他文件。");
            string folder = Path.Combine(dataRoot, "text-offers"); Directory.CreateDirectory(folder);
            string generated = Path.Combine(folder, "消息-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            // 只清理本功能生成、超过一天且没有有效邀请引用的副本，不触及用户附件。
            lock (gate)
            {
                foreach (string old in Directory.GetFiles(folder, "消息-*.txt"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1) && !offers.Values.Any(o => o.Path == old && o.Expires > DateTime.UtcNow)) File.Delete(old);
                byte[] bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text);
                if (Directory.GetFiles(folder).Sum(p => new FileInfo(p).Length) + bytes.Length > 64L * 1024 * 1024) throw new IOException("长文本缓存已达 64 MiB，请稍后重试或直接选择 TXT 文件发送。");
                try
                {
                    using (FileStream output = new FileStream(generated, FileMode.CreateNew, FileAccess.Write)) output.Write(bytes, 0, bytes.Length);
                    List<string> files = paths == null ? new List<string>() : new List<string>(paths); files.Add(generated);
                    if (relatives != null) { relatives = new Dictionary<string,string>(relatives, StringComparer.OrdinalIgnoreCase); relatives[generated] = null; }
                    return SendMessage(peer, "长文本已转为 TXT，打开附件查看完整内容。", files, null, relatives);
                }
                catch { if (File.Exists(generated)) File.Delete(generated); throw; }
            }
        }
        public ChatRecord SendMessage(Peer peer, string text, IList<string> paths)
        {
            return SendMessage(peer, text, paths, null);
        }
        public List<ChatRecord> SendBatchContent(Peer peer,string text,IEnumerable<string> paths,string root,Action<ChatRecord,int,int> sent)
        {
            int limit=Settings.BatchFileLimit;
            RequireSecurePeer(peer.DeviceId,peer.Fingerprint);
            List<SendBatch.Item> expanded=SendBatch.Expand(paths,limit);
            bool longText=MessageText.AsFile(text);
            if(longText&&expanded.Count>=limit)throw new IOException("超长正文需要一个 TXT 附件位置，请最多选择 "+(limit-1)+" 个文件。");
            lock(gate)CheckOfferCapacity(expanded.Count+(longText?1:0),null);
            VerifyPeer(peer);
            List<ChatRecord> records=new List<ChatRecord>();
            for(int offset=0;offset<expanded.Count;){
                TimeSpan deadline=clock.Elapsed+TimeSpan.FromSeconds(30);
                while(true){
                    lock(gate){if(disposed)throw new IOException("连接已断开，已发邀请保留。");if(pending.Count<64)break;}
                    if(clock.Elapsed>=deadline)throw new IOException("等待对方确认超时，已发邀请保留，请检查连接后继续。");
                    Thread.Sleep(25);
                }
                var batch=expanded.Skip(offset).Take(offset==0&&longText?19:20).ToList();
                ChatRecord record=SendContent(peer,offset==0?text:"",batch.Select(f=>f.Path).ToArray(),root,batch.ToDictionary(f=>f.Path,f=>f.Relative,StringComparer.OrdinalIgnoreCase));
                records.Add(record);offset+=batch.Count;sent(record,offset,expanded.Count);
            }
            return records;
        }
        public ChatRecord RetryMessage(Peer peer, ChatRecord original)
        {
            lock (gate)
            {
                if (!original.Outgoing || original.PeerId != peer.Id || !(original.State ?? "").Contains("未确认") || pending.ContainsKey(original.Packet))
                    throw new InvalidOperationException("此消息正在发送或已送达。");
            }
            Dictionary<string,string> relatives=(original.Transfers??new FileTransferState[0]).Where(f=>f.LocalPath!=null).ToDictionary(f=>f.LocalPath,f=>f.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            return SendMessage(peer, original.ForwardText(), original.GetLocalFiles(), original,relatives.Count==0?null:relatives);
        }
        private ChatRecord SendMessage(Peer peer, string text, IList<string> paths, ChatRecord original,Dictionary<string,string> relatives=null)
        {
            if (String.IsNullOrWhiteSpace(text) && (paths == null || paths.Count == 0)) throw new ArgumentException("请先输入消息或选择文件。");
            if ((text ?? "").IndexOf('\0') >= 0) throw new ArgumentException("消息不能包含空字符。");
            if (paths != null && paths.Count > Protocol.MaxAttachmentsPerMessage) throw new ArgumentException("一次最多发送 20 个文件。");
            Pending delivery;
            // 发现报文可以更新联系人地址，但不能改变已发消息的接收者、ACK 或附件授权。
            lock (gate) delivery = new Pending(peer);
            RequireSecurePeer(delivery.DeviceId,delivery.Fingerprint);
            long number = original == null ? Next() : original.Packet; List<Attachment> files = new List<Attachment>(); Dictionary<string, Offer> additions = new Dictionary<string, Offer>();
            if (paths != null)
            {
                int id = 0;
                foreach (string path in paths)
                {
                    FileInfo info = new FileInfo(Path.GetFullPath(path));
                    if (!info.Exists || !Protocol.SafeFileName(info.Name)) throw new IOException("文件不存在、文件名不安全或过长：" + info.Name);
                    Attachment file = new Attachment {
                        Id = id++, Name = info.Name, Size = info.Length, Modified = (long)(info.LastWriteTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds,
                            Sha256=SecureIdentity.HashFile(info.FullName)
                    };
                    files.Add(file);
                    if(relatives!=null)file.RelativePath=relatives[info.FullName];
                    additions[number + ":" + file.Id] = new Offer {
                        Path = info.FullName, Size = info.Length, Modified = info.LastWriteTimeUtc,
                            Expires = DateTime.UtcNow.AddDays(7), DeviceId=delivery.DeviceId,Fingerprint=delivery.Fingerprint,Sha256=file.Sha256
                    };
                }
            }
            uint command = Protocol.SendMessage | Protocol.SendCheck | Protocol.Utf8 | (files.Count > 0 ? Protocol.FileAttach : 0);
            byte[] bytes = Protocol.Encode(number, Login, Host, command, text, files.Count > 0 ? Protocol.FileList(files) : null, Settings.DeviceId,null,Security.Fingerprint);
            string display = text ?? "";
            if (files.Count > 0) display += (display.Length > 0 ? "\n" : "") + "[文件] " + String.Join("、", files.Select(f => f.Name));
            ChatRecord record = new ChatRecord {
                Packet = number, PeerId = peer.Id, Sender = Settings.Nickname, Text = display, Time = DateTime.Now, Outgoing = true, State = "等待确认",
                    LocalFiles = paths == null ? null : paths.Select(Path.GetFullPath).ToArray()
            };
            record.HasAttachments = files.Count > 0; record.AttachmentNames = files.Select(f => f.Name).ToArray();
            record.Transfers = files.Select(f => new FileTransferState {
                Id = f.Id, Name = f.Name, Size = f.Size, Modified=f.Modified,Sha256=f.Sha256,RelativePath=f.RelativePath,Status = "等待对方接收", LocalPath = record.LocalFiles[f.Id]
            }).ToArray();
            if (original != null)
            {
                if (original.Transfers != null && original.Transfers.Any(old => !files.Any(f => f.Id == old.Id && f.Name == old.Name && f.Size == old.Size)))
                    throw new IOException("原附件已改变，请作为新附件发送。");
                original.Transfers = record.Transfers; record = original;
            }
            foreach (Attachment file in files) { Offer offer = additions[number + ":" + file.Id]; offer.Record = record; offer.FileId = file.Id; }
            lock (gate)
            {
                if (disposed || udp == null) throw new IOException("尚未连接局域网。");
                if (pending.Count >= 256) throw new InvalidOperationException("等待送达的消息过多，请等待对方上线后继续。");
                CheckOfferCapacity(additions.Count,additions.Keys);
                // 写盘成功后才加入重试和文件授权；否则调用方收到失败后仍可能被后台补发。
                var nextOffers = new Dictionary<string,Offer>(offers);
                foreach (var item in additions) nextOffers[item.Key] = item.Value;
                SaveOffers(nextOffers);
                record.State = "等待确认";
                foreach (var item in additions) offers[item.Key] = item.Value;
                delivery.Bytes = bytes; delivery.Record = record; delivery.Last = DateTime.UtcNow; delivery.Attempts = 1;
                pending[number] = delivery;
            }
            Task.Run(()=>SendSecurePending(delivery));
            return record;
        }
    }
}
