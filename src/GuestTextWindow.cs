using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FeiqLight
{
    internal sealed class GuestTextWindow : ModernWindow
    {
        internal sealed class Item
        {
            internal GuestTransfer.TextEntry Entry;
            internal bool Published;
        }

        internal sealed class TextHistory : SmoothScrollView
        {
            internal readonly List<Item> Items=new List<Item>();
            private readonly List<Rectangle> rows=new List<Rectangle>();
            private int selectedIndex=-1;
            internal event Action SelectionChanged;
            internal event Action CopyRequested;
            internal Item SelectedItem { get { return selectedIndex<0?null:Items[selectedIndex]; } }
            internal int SelectedIndex
            {
                get { return selectedIndex; }
                set {
                    int next=Math.Max(-1,Math.Min(value,Items.Count-1));
                    if(next==selectedIndex)return;
                    selectedIndex=next;Invalidate();if(SelectionChanged!=null)SelectionChanged();
                }
            }
            internal TextHistory()
            {
                AccessibleName="会话文本列表";AccessibleRole=AccessibleRole.List;TabStop=true;
            }
            private static string DisplayText(Item item)
            {
                return MessageText.NormalizeLineBreaks(item.Entry.Text).Replace("\0","␀");
            }
            internal void Rebuild()
            {
                if(!IsHandleCreated||ClientSize.Width<40)return;
                rows.Clear();
                using(Graphics g=CreateGraphics()) {
                    float scale=g.DpiX/96F;
                    int gap=(int)Math.Round(10*scale),pad=(int)Math.Round(12*scale);
                    int width=Math.Max(40,ClientSize.Width-(int)Math.Round(18*scale));
                    int top=0;
                    foreach(Item item in Items) {
                        int textHeight=TextRenderer.MeasureText(g,DisplayText(item),Theme.Body,new Size(Math.Max(20,width-pad*2),100000),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding).Height;
                        int height=pad*2+(int)Math.Round(24*scale)+Math.Max(Theme.Body.Height,textHeight);
                        rows.Add(new Rectangle(0,top,width,height));top+=height+gap;
                    }
                    AutoScrollMinSize=new Size(ClientSize.Width,Math.Max(0,top-gap));
                }
                Invalidate();
            }
            internal void Clear()
            {
                Items.Clear();rows.Clear();SelectedIndex=-1;AutoScrollMinSize=Size.Empty;AutoScrollPosition=Point.Empty;Invalidate();
            }
            internal void ShowSelection()
            {
                if(selectedIndex<0||selectedIndex>=rows.Count)return;
                Rectangle row=rows[selectedIndex];int top=-AutoScrollPosition.Y;
                if(row.Top<top)AutoScrollPosition=new Point(0,row.Top);
                else if(row.Bottom>top+Height)AutoScrollPosition=new Point(0,Math.Max(row.Top,row.Bottom-Height));
            }
            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e);Rebuild(); }
            protected override void OnResize(EventArgs e) { base.OnResize(e);Rebuild(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g=e.Graphics;g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
                if(Items.Count==0) {
                    TextRenderer.DrawText(g,"等待访客发送文本\n也可在下方发布给访客",Theme.Body,ClientRectangle,Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
                    return;
                }
                float scale=g.DpiX/96F;int pad=(int)Math.Round(12*scale),heading=(int)Math.Round(24*scale);
                for(int index=0;index<rows.Count;index++) {
                    Rectangle row=rows[index];row.Offset(0,-ScrollOffset);
                    if(row.Bottom<0||row.Top>Height)continue;
                    Item item=Items[index];Color fill=item.Published?Theme.Ice:Theme.SurfaceContainerLow;
                    using(GraphicsPath path=Theme.Rounded(row,Math.Max(4,(int)Math.Round(8*scale)))) {
                        using(var brush=new SolidBrush(fill))g.FillPath(brush,path);
                        if(index==selectedIndex)using(var pen=new Pen(Theme.Blue,Math.Max(1,scale)))g.DrawPath(pen,path);
                    }
                    Rectangle title=new Rectangle(row.Left+pad,row.Top+pad,row.Width-pad*2,heading);
                    TextRenderer.DrawText(g,item.Published?"我已发布":"访客发来",Theme.Small,title,Theme.Muted,TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
                    Rectangle text=new Rectangle(title.Left,title.Bottom,title.Width,Math.Max(1,row.Bottom-title.Bottom-pad));
                    TextRenderer.DrawText(g,DisplayText(item),Theme.Body,text,Theme.Ink,TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
                }
                DrawScrollbar(g);
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                if(ScrollbarMouseDown(e))return;
                Focus();Point point=new Point(e.X,e.Y+ScrollOffset);
                for(int index=0;index<rows.Count;index++)if(rows[index].Contains(point)){SelectedIndex=index;break;}
                base.OnMouseDown(e);
            }
            protected override void OnMouseMove(MouseEventArgs e) { ScrollbarMouseMove(e);base.OnMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { ScrollbarMouseUp(e);base.OnMouseUp(e); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if(e.Control&&e.KeyCode==Keys.C){if(CopyRequested!=null)CopyRequested();e.Handled=true;}
                else if(e.KeyCode==Keys.Up){SelectedIndex=Math.Max(0,selectedIndex-1);ShowSelection();e.Handled=true;}
                else if(e.KeyCode==Keys.Down){SelectedIndex=Math.Min(Items.Count-1,selectedIndex+1);ShowSelection();e.Handled=true;}
                else if(e.KeyCode==Keys.Home){SelectedIndex=Items.Count==0?-1:0;ShowSelection();e.Handled=true;}
                else if(e.KeyCode==Keys.End){SelectedIndex=Items.Count-1;ShowSelection();e.Handled=true;}
                base.OnKeyDown(e);
            }
        }

        private readonly GuestTransfer session;
        private readonly Func<bool> current;
        private readonly Panel body=new Panel{Dock=DockStyle.Fill};
        private readonly Label summary=Theme.Label("收到 0 条 · 已发布 0 条",Theme.Section,Theme.Ink);
        private readonly Label privacy=Theme.Label("仅本次会话 · 结束自动清空 · HTTP 明文仅传非敏感文字",Theme.Small,Theme.Muted);
        private readonly RoundedPanel historyShell=new RoundedPanel{Radius=12,Border=Color.Empty};
        private readonly TextHistory history=new TextHistory();
        private readonly RoundedPanel composer=new RoundedPanel{Radius=12,Border=Color.Empty};
        private readonly Label draftLabel=Theme.Label("发布给访客",Theme.Section,Theme.Ink);
        private readonly Label textSize=Theme.Label("0 / 4096 UTF-8 字节",Theme.Small,Theme.Muted);
        private readonly RoundedPanel inputShell=new RoundedPanel{Radius=8,Border=Color.Empty};
        private readonly ChatEditor draft=new ChatEditor{BorderStyle=BorderStyle.None,ScrollBars=RichTextBoxScrollBars.None,DetectUrls=false,MaxLength=4096,Placeholder="输入或手动粘贴文本…",AccessibleName="发送给访客的纯文本"};
        private readonly Label status=Theme.Label("每方向最多 64 条 / 128 KiB，点击文本卡片后可复制",Theme.Small,Theme.Muted);
        private readonly Button send,copy;
        private readonly Timer timer=new Timer{Interval=250};
        private readonly ToolTip tips=new ToolTip();
        private int receivedCount,publishedCount;
        private bool statusError;

        internal GuestTextWindow(GuestTransfer session,Func<bool> current)
        {
            this.session=session;this.current=current;
            Text="文本互传";ClientSize=new Size(560,560);MinimumSize=new Size(440,480);UseSecondaryChrome();
            privacy.AutoEllipsis=true;status.AutoEllipsis=true;textSize.TextAlign=ContentAlignment.MiddleRight;
            historyShell.Controls.Add(history);composer.Controls.Add(draftLabel);composer.Controls.Add(textSize);composer.Controls.Add(inputShell);
            draft.Dock=DockStyle.Fill;draft.Font=Theme.Body;inputShell.Controls.Add(draft);
            inputShell.MouseDown+=delegate{draft.Focus();};inputShell.MouseWheel+=(sender,e)=>draft.ScrollByWheel(e.Delta);
            Theme.AttachEditMenu(draft);
            draft.Enter+=delegate{inputShell.Border=Theme.Blue;inputShell.Invalidate();};
            draft.Leave+=delegate{inputShell.Border=Color.Empty;inputShell.Invalidate();};
            draft.TextChanged+=delegate{textSize.Text=Encoding.UTF8.GetByteCount(draft.Text)+" / 4096 UTF-8 字节";};
            send=Theme.Button("发布给访客",delegate{Publish();},true);
            copy=Theme.Button("复制文本",delegate{Copy();},false);copy.Enabled=false;
            ((RoundedButton)copy).Glyph="copy";((RoundedButton)copy).ShowLabel=true;
            history.SelectionChanged+=delegate{copy.Enabled=history.SelectedItem!=null;};history.CopyRequested+=Copy;
            body.Controls.Add(summary);body.Controls.Add(privacy);body.Controls.Add(historyShell);body.Controls.Add(composer);body.Controls.Add(status);body.Controls.Add(copy);body.Controls.Add(send);Surface.Controls.Add(body);
            body.SizeChanged+=delegate{Relayout();};DpiChangedAfterParent+=delegate{Relayout();};
            Theme.Changed+=ApplyColors;session.TextEnded+=SessionEnded;
            timer.Tick+=delegate{RefreshTexts();};timer.Start();Shown+=delegate{Relayout();RefreshTexts();};ApplyColors();Relayout();
        }
        private void Relayout()
        {
            if(IsDisposed||body.ClientSize.Width<100||body.ClientSize.Height<100)return;
            float scale;using(Graphics g=CreateGraphics())scale=g.DpiX/96F;
            int pad=(int)Math.Round(16*scale),gap=(int)Math.Round(10*scale),width=body.Width-pad*2;
            int heading=(int)Math.Round(22*scale),privacyHeight=(int)Math.Round(20*scale);
            summary.Bounds=new Rectangle(pad,pad,width,heading);privacy.Bounds=new Rectangle(pad,summary.Bottom,width,privacyHeight);
            int buttonHeight=(int)Math.Round(36*scale),buttonGap=(int)Math.Round(10*scale);
            using(Graphics g=CreateGraphics()) {
                int sendWidth=Math.Max((int)Math.Round(116*scale),TextRenderer.MeasureText(g,send.Text,send.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix).Width+(int)Math.Round(28*scale));
                int copyWidth=Math.Max((int)Math.Round(116*scale),TextRenderer.MeasureText(g,copy.Text,copy.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix).Width+(int)Math.Round(48*scale));
                send.Bounds=new Rectangle(body.Width-pad-sendWidth,body.Height-pad-buttonHeight,sendWidth,buttonHeight);
                copy.Bounds=new Rectangle(send.Left-buttonGap-copyWidth,send.Top,copyWidth,buttonHeight);
            }
            status.Bounds=new Rectangle(pad,send.Top-(int)Math.Round(28*scale),width,(int)Math.Round(24*scale));
            int composerHeight=Math.Max((int)Math.Round(108*scale),Math.Min((int)Math.Round(154*scale),(int)Math.Round(body.Height*.30)));
            composer.Bounds=new Rectangle(pad,status.Top-gap-composerHeight,width,composerHeight);
            historyShell.Bounds=new Rectangle(pad,privacy.Bottom+gap,width,Math.Max(60,composer.Top-gap-privacy.Bottom-gap));
            int inner=(int)Math.Round(12*scale);
            history.Bounds=new Rectangle(inner,inner,Math.Max(20,historyShell.Width-inner*2),Math.Max(20,historyShell.Height-inner*2));
            draftLabel.Bounds=new Rectangle(inner,inner,Math.Max(80,composer.Width/2-inner),heading);
            textSize.Bounds=new Rectangle(composer.Width/2,inner,Math.Max(80,composer.Width/2-inner),heading);
            inputShell.Padding=new Padding(inner,(int)Math.Round(8*scale),inner,(int)Math.Round(8*scale));
            inputShell.Bounds=new Rectangle(inner,draftLabel.Bottom+(int)Math.Round(6*scale),composer.Width-inner*2,Math.Max(34,composer.Height-draftLabel.Bottom-(int)Math.Round(6*scale)-inner));
            history.Rebuild();
        }
        private void RefreshTexts()
        {
            if(IsDisposed)return;
            if(!current()){ClearAndClose();return;}
            GuestTransfer.TextEntry[] received=session.Texts(false),published=session.Texts(true);
            if(received.Length!=receivedCount||published.Length!=publishedCount) {
                foreach(var entry in received.Skip(receivedCount))history.Items.Add(new Item{Entry=entry});
                foreach(var entry in published.Skip(publishedCount))history.Items.Add(new Item{Entry=entry,Published=true});
                history.Rebuild();receivedCount=received.Length;publishedCount=published.Length;
                if(history.SelectedIndex<0&&history.Items.Count>0)history.SelectedIndex=0;
            }
            summary.Text="收到 "+receivedCount+" / 64 条 · 已发布 "+publishedCount+" / 64 条";
        }
        private void Publish()
        {
            if(!current()){ClearAndClose();return;}
            if(draft.IsComposing)return;
            try{session.PublishText(draft.Text);statusError=false;draft.Clear();status.Text="已发布给本次会话的访客";RefreshTexts();history.SelectedIndex=history.Items.FindLastIndex(item=>item.Published);history.ShowSelection();draft.Focus();}
            catch(ToolsHttp.HttpError error){statusError=true;status.Text=error.Message;}
            tips.SetToolTip(status,status.Text);ApplyColors();
        }
        private void Copy()
        {
            if(!current()){ClearAndClose();return;}
            Item item=history.SelectedItem;if(item==null)return;
            try{Clipboard.SetText(item.Entry.Text);statusError=false;status.Text="已复制选中文本";}
            catch(ExternalException){statusError=true;status.Text="剪贴板正被占用，请稍后重试复制";}
            tips.SetToolTip(status,status.Text);ApplyColors();
        }
        private void SessionEnded()
        {
            if(IsDisposed||Disposing)return;
            if(InvokeRequired){if(IsHandleCreated)try{BeginInvoke((Action)ClearAndClose);}catch(InvalidOperationException){if(!IsDisposed&&!Disposing)throw;}return;}
            ClearAndClose();
        }
        internal void ClearAndClose()
        {
            if(IsDisposed)return;
            timer.Stop();history.Clear();draft.Clear();summary.Text="会话已结束";status.Text="";Close();
        }
        private void ApplyColors()
        {
            if(IsDisposed)return;
            body.BackColor=Surface.BackColor=summary.BackColor=privacy.BackColor=status.BackColor=Theme.Canvas;
            summary.ForeColor=Theme.Ink;privacy.ForeColor=Theme.Muted;status.ForeColor=statusError?Theme.Danger:Theme.Muted;
            historyShell.BackColor=composer.BackColor=Theme.Canvas;historyShell.Fill=composer.Fill=Theme.Surface;history.BackColor=Theme.Surface;
            draftLabel.BackColor=textSize.BackColor=inputShell.BackColor=Theme.Surface;draftLabel.ForeColor=Theme.Ink;textSize.ForeColor=Theme.Muted;
            inputShell.Fill=draft.BackColor=Theme.Ice;draft.ForeColor=Theme.Ink;
            send.BackColor=Theme.Blue;send.ForeColor=Color.White;copy.BackColor=Theme.Surface;copy.ForeColor=Theme.Ink;
            ((RoundedButton)send).Radius=((RoundedButton)copy).Radius=8;
            ((RoundedButton)copy).Border=Color.Empty;((RoundedButton)copy).HoverBack=Theme.Hover;((RoundedButton)copy).HoverFore=Theme.Ink;
            history.Invalidate();historyShell.Invalidate();composer.Invalidate();inputShell.Invalidate();
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing&&!IsDisposed){session.TextEnded-=SessionEnded;Theme.Changed-=ApplyColors;timer.Stop();timer.Dispose();tips.Dispose();history.Clear();draft.Clear();}
            base.Dispose(disposing);
        }
    }
}
