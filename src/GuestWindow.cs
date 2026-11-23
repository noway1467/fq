using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    internal sealed class GuestWindow : ModernWindow
    {
        private readonly MainForm main;
        private readonly LanService network;
        private readonly ListBox files=new ListBox();
        private readonly ComboBox address=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList };
        private readonly ToggleSwitch upload;
        private readonly Label state=Theme.Label("尚未开启",Theme.Small,Theme.Muted);
        private readonly Button start,choose,copy,qr;
        private readonly Timer timer=new Timer { Interval=1000 };
        private GuestTransfer session;
        internal GuestWindow(MainForm main,LanService network)
        {
            this.main=main;this.network=network;Text="临时浏览器传输";ClientSize=new Size(720,540);MinimumSize=new Size(620,460);UseSecondaryChrome();
            var body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(24,14,24,18),Margin=Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int h in new[]{56,42,-1,64,38,38,46})body.RowStyles.Add(new RowStyle(h<0?SizeType.Percent:SizeType.Absolute,h<0?100:h));Surface.Controls.Add(body);
            Label description=Theme.Label("对方无需安装飞Q，用浏览器扫码或打开链接即可传文件。\n链接15分钟后失效；HTTP明文，仅用于可信局域网中的非敏感文件。",Theme.Small,Theme.Muted);description.Dock=DockStyle.Fill;body.Controls.Add(description,0,0);
            var toolbar=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty };
            choose=Theme.Button("选择文件",delegate {using(var picker=new OpenFileDialog{Multiselect=true,Title="选择临时分享的文件"})if(picker.ShowDialog(this)==DialogResult.OK){files.Items.Clear();files.Items.AddRange(picker.FileNames);}},false);choose.Width=112;toolbar.Controls.Add(choose);
            Button remove=Theme.IconButton("close","移除所选文件",delegate {if(session==null&&files.SelectedIndex>=0)files.Items.RemoveAt(files.SelectedIndex);},false);toolbar.Controls.Add(remove);body.Controls.Add(toolbar,0,1);
            files.Dock=DockStyle.Fill;files.BorderStyle=BorderStyle.None;files.BackColor=Theme.Surface;files.ForeColor=Theme.Ink;files.ItemHeight=30;files.IntegralHeight=false;files.Font=Theme.Body;files.AccessibleName="临时分享文件";body.Controls.Add(files,0,2);
            upload=new ToggleSwitch { Dock=DockStyle.Fill,Text="允许对方向我发送文件",AccessibleName="允许浏览器上传",Description="保存到当前接收文件夹；本次最多100个文件、合计10 GiB。",Checked=false };body.Controls.Add(upload,0,3);
            address.Dock=DockStyle.Fill;address.Font=Theme.Body;address.BackColor=Theme.Ice;address.ForeColor=Theme.Ink;address.AccessibleName="临时传输网络地址";
            foreach(string ip in network.LoopbackOnly?new[]{"127.0.0.1"}:LanNetwork.Scan().Select(l=>l.Address.ToString()).Distinct().ToArray())address.Items.Add(ip);if(address.Items.Count>0)address.SelectedIndex=0;body.Controls.Add(address,0,4);
            state.Dock=DockStyle.Fill;state.AutoEllipsis=true;body.Controls.Add(state,0,5);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty };
            start=Theme.Button("开启临时传输",delegate {Toggle();},true);start.Width=140;actions.Controls.Add(start);
            qr=Theme.Button("显示二维码",delegate {try{System.Diagnostics.Process.Start(Link()+"&qr");}catch(Exception e){state.Text=e.Message;}},false);qr.Width=120;actions.Controls.Add(qr);
            copy=Theme.Button("复制链接",delegate {try{Clipboard.SetText(Link());state.Text="链接已复制 · "+StateText();}catch(Exception e){state.Text=e.Message;}},false);copy.Width=108;actions.Controls.Add(copy);body.Controls.Add(actions,0,6);
            timer.Tick+=delegate {if(session!=null&&!session.Active){Stop();state.Text="本次临时传输已结束";}else if(session!=null)state.Text=StateText();};timer.Start();UpdateActions();
        }
        private string Link(){if(session==null||!session.Active)throw new IOException("请先开启临时传输。");return session.Url((string)address.SelectedItem);}
        private string StateText(){return "剩余 "+Math.Max(0,(int)Math.Ceiling((session.Until-DateTime.UtcNow).TotalMinutes))+" 分钟 · 已接收 "+session.SavedFiles.Length+" 个文件";}
        private void Toggle()
        {
            if(session!=null){Stop();return;}
            try {
                if(address.SelectedItem==null)throw new IOException("没有可用的局域网，请先连接 Wi-Fi、热点或以太网。");
                session=new GuestTransfer(files.Items.Cast<string>(),network.Settings.ReceiveFolder,upload.Checked,network.LoopbackOnly);
                session.Received=(path,name)=>main.Ui(()=>main.GuestReceived(path,name));state.Text=StateText();UpdateActions();
            }catch(Exception e){state.Text=e.Message;}
        }
        private void UpdateActions(){bool active=session!=null;start.Text=active?"结束临时传输":"开启临时传输";choose.Enabled=files.Enabled=upload.Enabled=!active;copy.Enabled=qr.Enabled=active;}
        internal void Stop(){if(session!=null){session.Dispose();state.Text=session.CleanupError??"临时传输已结束，完整文件保留";session=null;}UpdateActions();}
        protected override void Dispose(bool disposing){if(disposing){timer.Dispose();if(session!=null)session.Dispose();}base.Dispose(disposing);}
    }
}
