using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FeiqLight
{
    internal static class LanNetwork
    {
        internal sealed class Link
        {
            internal IPAddress Address, Mask;
            internal int Index;
            internal bool Contains(IPAddress address)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) return false;
                byte[] a = Address.GetAddressBytes(), b = address.GetAddressBytes(), m = Mask.GetAddressBytes();
                return Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (b[i] & m[i]));
            }
            internal IPAddress Broadcast { get { byte[] a=Address.GetAddressBytes(),m=Mask.GetAddressBytes();for(int i=0;i<4;i++)a[i]|=(byte)~m[i];return new IPAddress(a); } }
        }
        internal static bool Eligible(NetworkInterfaceType type, string description)
        {
            if(type!=NetworkInterfaceType.Ethernet && type!=NetworkInterfaceType.Wireless80211 && type!=NetworkInterfaceType.GigabitEthernet && type!=NetworkInterfaceType.FastEthernetFx && type!=NetworkInterfaceType.FastEthernetT)return false;
            string text=(description??"").ToLowerInvariant();
            return !new[]{"vpn","wireguard","wintun","tap-windows","tunnel","vmware","virtualbox","hyper-v"}.Any(text.Contains);
        }
        internal static List<Link> Scan()
        {
            List<Link> links=new List<Link>();
            foreach(NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if(adapter.OperationalStatus!=OperationalStatus.Up || !Eligible(adapter.NetworkInterfaceType,adapter.Name+" "+adapter.Description))continue;
                IPInterfaceProperties properties=adapter.GetIPProperties();IPv4InterfaceProperties ipv4=properties.GetIPv4Properties();if(ipv4==null)continue;
                foreach(UnicastIPAddressInformation item in properties.UnicastAddresses)
                {
                    if(item.Address.AddressFamily!=AddressFamily.InterNetwork || item.IPv4Mask==null || !Private(item.Address))continue;
                    Link link=new Link{Address=item.Address,Mask=item.IPv4Mask,Index=ipv4.Index};
                    byte[] mask=item.IPv4Mask.GetAddressBytes();uint bits=((uint)mask[0]<<24)|((uint)mask[1]<<16)|((uint)mask[2]<<8)|mask[3];uint inverse=~bits;
                    if(inverse<3 || (inverse&(inverse+1))!=0 || !Private(link.Broadcast))continue;
                    byte[] network=item.Address.GetAddressBytes();for(int i=0;i<4;i++)network[i]&=mask[i];if(!Private(new IPAddress(network)))continue;
                    links.Add(link);
                }
            }
            return links;
        }
        private static bool Private(IPAddress address){byte[] b=address.GetAddressBytes();return b.Length==4&&(b[0]==10||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168);}
        internal static Link Route(IEnumerable<Link> links,IPAddress address){return links.Where(l=>l.Contains(address)).OrderByDescending(l=>BitConverter.ToUInt32(l.Mask.GetAddressBytes().Reverse().ToArray(),0)).FirstOrDefault();}
        internal static string Key(IEnumerable<Link> links){return String.Join(";",links.Select(l=>l.Index+":"+l.Address+"/"+l.Mask).OrderBy(s=>s,StringComparer.Ordinal));}
    }
}
