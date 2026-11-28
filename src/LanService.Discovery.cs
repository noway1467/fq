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
        public static List<IPAddress> LocalIPv4()
        {
            return LanNetwork.Scan().Select(l=>l.Address).Distinct().ToList();
        }
        private static HashSet<IPAddress> CurrentAddresses() { HashSet<IPAddress> result = new HashSet<IPAddress>(LocalIPv4()); result.Add(IPAddress.Loopback); return result; }

        private byte[] WithPort(byte[] bytes)
        {
            byte[] suffix = System.Text.Encoding.ASCII.GetBytes("FQPORT=" + Port + "\0");
            byte[] wire = new byte[bytes.Length + suffix.Length];
            Buffer.BlockCopy(bytes, 0, wire, 0, bytes.Length);
            Buffer.BlockCopy(suffix, 0, wire, bytes.Length, suffix.Length);
            return wire;
        }
        public void Probe(IPEndPoint endpoint) { Probe(endpoint, false); }
        private void Probe(IPEndPoint endpoint, bool broadcast)
        {
            if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("目前仅支持 IPv4。");
            bool allowed = LoopbackOnly ? IPAddress.IsLoopback(endpoint.Address) : LanNetwork.Route(links, endpoint.Address) != null;
            bool schedule = !broadcast && tcp != null && !disposed && HeartbeatRetry >= TimeSpan.FromSeconds(1) && allowed;
            try
            {
                Send(Encode(Protocol.Entry | Protocol.CapUtf8 | Protocol.FileAttach, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group)), endpoint);
            }
            finally
            {
                if (schedule)
                {
                    lock (gate) { if (pendingTcpTargets.Count < 256) pendingTcpTargets.Add(endpoint); }
                    ScheduleTcpFallback();
                }
            }
        }
        public void Refresh()
        {
            // 切换网络后本机地址会变，先更新自身过滤再广播，避免把自己的新地址当成联系人。
            lastRefresh = DateTime.UtcNow;
            if (!LoopbackOnly)
            {
                List<LanNetwork.Link> next=NetworkLinks();string key=LanNetwork.Key(next);
                bool reset=networkKey!=null&&networkKey!=key;lock(sendGate){links=next;networkKey=key;}
                localAddresses=new HashSet<IPAddress>(next.Select(l=>l.Address));localAddresses.Add(IPAddress.Loopback);
                if(reset)ResetNetwork();
                foreach (IPAddress address in next.Select(l=>l.Broadcast).Distinct())
                    try { Probe(new IPEndPoint(address, Port), true); } catch (SocketException e) { Report("广播失败：" + e.Message); }
            }
            // 单播补充广播；只有收到真实报文才置为在线，不把历史地址伪装成在线联系人。
            List<Peer> known = Peers;
            IEnumerable<IPEndPoint> targets = known.Select(p => p.Endpoint).Concat(AppSettings.NormalizeKnownPeers(Settings.KnownPeers).Select(AppSettings.ParseKnownPeer)).Distinct();
            foreach (IPEndPoint target in targets)
            {
                if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) continue;
                if(!LoopbackOnly&&LanNetwork.Route(links,target.Address)==null)continue;
                try { Probe(target); } catch (SocketException e) { Report("刷新联系人失败：" + e.Message); }
            }
        }
        private void ResetNetwork()
        {
            if(Security!=null)Security.CancelConfirmations();
            List<Pending> interrupted;
            lock(gate) {
                foreach(Peer peer in peers.Values){peer.Online=false;peer.Misses=0;}
                interrupted=pending.Values.ToList();pending.Clear();
                foreach(TcpClient client in connections)client.Close();
                foreach(TcpClient client in probeSockets)client.Close();
                foreach(PendingDownload pd in pendingDownloads){pd.Ready.Set();pd.Done.Set();}pendingDownloads.Clear();
                CancelReverseRequests();
                pendingTcpTargets.Clear();
            }
            foreach(Pending item in interrupted)Complete(item.Record,"网络已切换，未确认送达");
            Action reset=NetworkReset;if(reset!=null)reset();Changed();
        }
        // 网络切换或用户刷新时立即复核：仍能回应的联系人保持在线，失联者约 6 秒内离线。
        public void Revalidate()
        {
            lock (gate)
            {
                verifyFrom = clock.Elapsed;
                foreach (Peer peer in peers.Values)
                    if (peer.Misses == 0) peer.Probed = verifyFrom - HeartbeatRetry;
            }
            Refresh();
        }
        // 系统地址通知常成批到达，只记标记，由每秒维护合并处理。
        private void OnNetworkChanged(object sender, EventArgs e) { Interlocked.Exchange(ref networkDirty, 1); }
        private Peer Upsert(Packet packet, IPEndPoint endpoint)
        {
            string legacyId = endpoint + "/" + packet.Login; Peer peer;
            bool entry = packet.Mode == Protocol.Entry || packet.Mode == Protocol.AnswerEntry || packet.Mode == Protocol.Absence;
            lock (gate)
            {
                if (disposed) return null;
                peer = packet.DeviceId == null ? null : peers.Values.FirstOrDefault(p => p.DeviceId == packet.DeviceId);
                if(Security!=null&&packet.DeviceId!=null) {
                    string pin=Security.TrustedFingerprint(packet.DeviceId);
                    if(pin!=null&&packet.Fingerprint!=pin)return null;
                }
                // 仅升级端点和登录名均匹配、且尚无稳定身份的旧会话，不按昵称猜测设备。
                if (peer == null) peer = peers.Values.FirstOrDefault(p => p.Endpoint.Equals(endpoint) && p.Login == packet.Login && (packet.DeviceId == null
                    || p.DeviceId == null || p.DeviceId == packet.DeviceId));
                // 旧格式可按地址匹配联系人，但省略身份不能绕过该会话已经保存的证书。
                if (Security != null && peer != null && Security.TrustedFingerprint(peer.DeviceId) != null &&
                    (packet.DeviceId != peer.DeviceId || packet.Fingerprint != Security.TrustedFingerprint(peer.DeviceId))) return null;
                if (peer == null)
                {
                    if (peers.Count >= 2000) return null;
                    peer = new Peer {
                        ConversationId = packet.DeviceId == null ? legacyId : "device:" + packet.DeviceId, DeviceId = packet.DeviceId, Endpoint = endpoint,
                            Login = packet.Login, Host = packet.Host, Nickname = packet.Login, Group = "我的局域网"
                    };
                    peers[peer.Id] = peer;
                }
                peer.ConversationId = peer.Id; peer.DeviceId = packet.DeviceId ?? peer.DeviceId;
                if(entry||packet.Fingerprint!=null)peer.Fingerprint=packet.Fingerprint;
                peer.Endpoint = endpoint; peer.Login = packet.Login; peer.Host = packet.Host;
                if (entry)
                {
                    if(packet.Avatar != null) peer.Avatar = packet.Avatar;
                    peer.Nickname = String.IsNullOrWhiteSpace(packet.Body) ? packet.Login : Protocol.Prefix(packet.Body, 128);
                    if (!String.IsNullOrWhiteSpace(packet.Extra) || String.IsNullOrEmpty(peer.Group))
                        peer.Group = String.IsNullOrWhiteSpace(packet.Extra) ? "我的局域网" : Protocol.Prefix(packet.Extra, 128);
                }
                peer.Utf8 = peer.Utf8 || (packet.Command & (Protocol.Utf8 | Protocol.CapUtf8)) != 0;
                peer.Online = packet.Mode != Protocol.Exit; peer.LastSeen = DateTime.UtcNow; peer.Seen = clock.Elapsed; peer.Misses = 0;
            }
            Changed(); return peer;
        }
        private bool IsSelf(Packet packet, IPEndPoint remote)
        {
            return (Settings.DeviceId != null && packet.DeviceId == Settings.DeviceId) || ((packet.ReplyPort > 0 ? packet.ReplyPort : remote.Port) == Port
                && localAddresses.Contains(remote.Address) && packet.Login == Login && packet.Host == Host);
        }

        private void Tick(object ignored)
        {
            if (disposed) return;
            try
            {
                TimeSpan maintenance=clock.Elapsed;
                if(!LoopbackOnly&&maintenance-lastMaintenance>TimeSpan.FromSeconds(30)){
                    ResetNetwork();
                    Interlocked.Exchange(ref networkDirty,1);
                }
                lastMaintenance=maintenance;
                List<Pending> retry = new List<Pending>(), failed = new List<Pending>();
                List<IPEndPoint> probes = new List<IPEndPoint>();
                List<Peer> syncTargets = new List<Peer>();
                bool changed = false;
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow; TimeSpan elapsed = clock.Elapsed;
                    foreach (Pending item in pending.Values.ToList())
                    {
                        if (item.Canceled || item.Sending!=0 || item.Transmitting!=0 || item.Delivered || (now - item.Last).TotalSeconds < 2) continue;
                        if (item.Attempts >= 4) { pending.Remove(item.Record.Packet); item.Record.State="未确认送达，请检查网络后重发"; failed.Add(item); }
                        else { item.Attempts++; item.Last = now; retry.Add(item); }
                    }
                    foreach (string key in seen.Where(p => (now - p.Value).TotalMinutes > 10).Select(p => p.Key).ToList()) seen.Remove(key);
                    foreach (string key in offers.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList()) { EndOffer(offers[key], "邀请已过期，请重新发送"); offers.Remove(key); }
                    // 切网不会发送退出通知；只按实际发出且未获回应的探测判离线，计时器延后不会误判。
                    foreach (Peer peer in peers.Values)
                    {
                        if (!peer.Online)
                        {
                            if (elapsed - peer.Probed >= HeartbeatRetry) { peer.Probed = elapsed; probes.Add(peer.Endpoint); }
                            continue;
                        }
                        if (Security != null && HeartbeatRetry >= TimeSpan.FromSeconds(1) && peer.DeviceId != null && peer.Fingerprint != null
                            && Security.IsTrusted(peer.DeviceId, peer.Fingerprint)) syncTargets.Add(peer);
                        bool verify = peer.Seen < verifyFrom;
                        if ((!verify && elapsed - peer.Seen < HeartbeatIdle) || elapsed - peer.Probed < HeartbeatRetry) continue;
                        if (peer.Misses >= (verify ? VerifyMisses : IdleMisses)) { peer.Online = false; peer.Misses = 0; changed = true; }
                        else { peer.Probed = elapsed; peer.Misses++; probes.Add(peer.Endpoint); }
                    }
                }
                foreach (Pending item in retry) if(!item.Canceled&&!item.Delivered)Task.Run(()=>SendSecurePending(item));
                foreach (IPEndPoint target in probes) try { Probe(target); } catch (SocketException) { } catch(IOException) { }
                foreach (Peer target in syncTargets) ScheduleSync(target);
                foreach (Pending item in failed) NotifyDelivery(item.Record);
                if (changed) Changed();
                if (Interlocked.Exchange(ref networkDirty, 0) != 0) Revalidate();
                else if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 30) Refresh();
            }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Report("网络维护失败：" + e.Message); }
        }
        private void ScheduleTcpFallback()
        {
            if (disposed || tcp == null || Interlocked.CompareExchange(ref tcpFallbackScheduled, 1, 0) != 0) return;
            Task.Delay(150).ContinueWith(_ => RunTcpFallback(), TaskScheduler.Default);
        }
        private void RunTcpFallback()
        {
            Interlocked.Exchange(ref tcpFallbackScheduled, 0);
            if (disposed || tcp == null) return;
            List<IPEndPoint> batch = new List<IPEndPoint>();
            lock (gate)
            {
                if (pendingTcpTargets.Count == 0) return;
                foreach (IPEndPoint target in pendingTcpTargets)
                    if (activeTcpProbes.Add(target)) batch.Add(target);
                pendingTcpTargets.Clear();
            }
            foreach (IPEndPoint target in batch)
            {
                Task.Run(() =>
                {
                    probeConcurrency.Wait();
                    try { ProbeTcp(target); }
                    finally { probeConcurrency.Release(); lock (gate) activeTcpProbes.Remove(target); }
                });
            }
        }
        private void ProbeTcp(IPEndPoint target)
        {
            if (disposed || tcp == null) return;
            if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) return;
            if (!LoopbackOnly && LanNetwork.Route(links, target.Address) == null) return;
            Peer known = null;
            lock (gate)
            {
                if (disposed) return;
                known = peers.Values.FirstOrDefault(p => p.Endpoint.Equals(target));
                TimeSpan elapsed = clock.Elapsed;
                if (known != null && known.Online && known.Misses == 0 && known.Seen >= verifyFrom && elapsed - known.Seen < TimeSpan.FromSeconds(1)) return;
            }
            bool connected = false, discovered = false;
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            lock (gate) { if (disposed) { client.Close(); return; } probeSockets.Add(client); }
            try
            {
                using (client)
                {
                    BindClient(client, target.Address);
                    Task connecting = client.ConnectAsync(target.Address, target.Port);
                    connecting.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (!connecting.Wait(1200)) return;
                    connected = true;
                    client.ReceiveTimeout = 1500; client.SendTimeout = 1500;
                    NetworkStream stream = client.GetStream();
                    stream.Write(new byte[] { 70, 81, 68, 10 }, 0, 4);
                    SecureIdentity.WriteFrame(stream, WithPort(Encode(Protocol.Entry | Protocol.CapUtf8 | Protocol.FileAttach, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group))));
                    Packet packet;
                    if (Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream, 1500), 60000), out packet)
                        && (packet.Mode == Protocol.AnswerEntry || packet.Mode == Protocol.Entry || packet.Mode == Protocol.Absence))
                    {
                        IPEndPoint from = new IPEndPoint(target.Address, packet.ReplyPort > 0 ? packet.ReplyPort : target.Port);
                        if (!IsSelf(packet, from)) { Peer latest = Upsert(packet, from); if (latest != null) { known = latest; discovered = true; } }
                    }
                }
            }
            catch (Exception) { }
            finally { lock (gate) probeSockets.Remove(client); }
            if (connected && known != null && Security != null && known.Fingerprint != null && Security.IsTrusted(known.DeviceId, known.Fingerprint))
            {
                if (!discovered) try { VerifyPeer(known); } catch (Exception) { }
                ScheduleSync(known);
            }
        }

        private void ServeDiscovery(TcpClient client, Stream input)
        {
            Packet packet;
            if (!Protocol.TryParse(SecureIdentity.ReadFrame(input, 60000), out packet)) return;
            if (packet.Mode != Protocol.Entry && packet.Mode != Protocol.AnswerEntry && packet.Mode != Protocol.Absence) return;
            IPEndPoint from = new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint).Address, packet.ReplyPort > 0 ? packet.ReplyPort : Port);
            if (IsSelf(packet, from)) return;
            Peer peer = Upsert(packet, from);
            if (peer == null) return;
            if (packet.Mode == Protocol.Entry)
            {
                uint flags = Protocol.AnswerEntry | Protocol.CapUtf8 | Protocol.FileAttach | (peer.Utf8 ? Protocol.Utf8 : 0);
                SecureIdentity.WriteFrame(client.GetStream(), WithPort(Encode(flags, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group))));
            }
        }
    }
}
