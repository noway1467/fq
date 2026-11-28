using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight.Tests
{
    internal static class ProtocolVectorTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
        private static string Text(Dictionary<string, object> value, string key) { return value.ContainsKey(key) ? (string)value[key] : null; }
        private static long Number(Dictionary<string, object> value, string key) { return Convert.ToInt64(value[key]); }
        private static void Equal(object expected, object actual, string context)
        {
            var map = expected as Dictionary<string, object>;
            if (map != null) {
                var other = actual as Dictionary<string, object>;
                if (other == null || map.Count != other.Count) throw new Exception(context + " 对象字段不一致");
                foreach (var entry in map) { if (!other.ContainsKey(entry.Key)) throw new Exception(context + " 缺少 " + entry.Key); Equal(entry.Value, other[entry.Key], context + "." + entry.Key); }
            } else if (expected is object[]) {
                var list = ((IEnumerable)actual).Cast<object>().ToArray(); var wanted = (object[])expected;
                if (list.Length != wanted.Length) throw new Exception(context + " 附件数量 " + list.Length + "，预期 " + wanted.Length);
                for (int i = 0; i < list.Length; i++) Equal(wanted[i], list[i], context + "[" + i + "]");
            } else if (expected is int || expected is long || expected is decimal) {
                if (actual == null || Convert.ToDecimal(expected) != Convert.ToDecimal(actual)) throw new Exception(context + " 数值不一致");
            } else if (!Object.Equals(expected, actual)) throw new Exception(context + " 结果不一致：" + Json.Serialize(actual));
        }
        private static object PacketValue(byte[] bytes)
        {
            Packet p; if (!Protocol.TryParse(bytes, out p)) return null;
            return new Dictionary<string, object> {
                {"number",p.Number},{"login",p.Login},{"host",p.Host},{"command",(long)p.Command},
                {"body",p.Body},{"extra",p.Extra},{"deviceId",p.DeviceId},{"avatar",p.Avatar},
                {"fingerprint",p.Fingerprint},{"replyPort",p.ReplyPort},{"reverseConnections",p.ReverseConnections}
            };
        }
        private static object[] FilesValue(IEnumerable<Attachment> files)
        {
            return files.Select(f => (object)new Dictionary<string, object> {
                {"id",f.Id},{"name",f.Name},{"size",f.Size},{"modified",f.Modified},{"sha256",f.Sha256},{"relativePath",f.RelativePath}
            }).ToArray();
        }
        private static object Execute(Dictionary<string, object> test)
        {
            object input = test["input"];
            switch (Text(test, "op")) {
                case "field": return Protocol.Field((string)input);
                case "prefix": return Protocol.Prefix((string)input, (int)Number(test, "length"));
                case "device": return Protocol.NormalizeDeviceId((string)input);
                case "pair": return SecureIdentity.PairCode((string)input, Text(test, "second"));
                case "avatar": return Protocol.NormalizeAvatar((string)input);
                case "name": return Protocol.SafeFileName((string)input);
                case "path": return Protocol.SafeRelativePath((string)input);
                case "packet": return PacketValue(Convert.FromBase64String((string)input));
                case "encode":
                    var p = Map(input);
                    string body=p.ContainsKey("bodyUtf16")?new string(((object[])p["bodyUtf16"]).Select(c=>(char)Convert.ToInt32(c)).ToArray()):Text(p,"body");
                    byte[] encoded = Protocol.Encode(Number(p,"number"),Text(p,"login"),Text(p,"host"),(uint)Number(p,"command"),body,Text(p,"extra"),Text(p,"deviceId"),Text(p,"avatar"),Text(p,"fingerprint"));
                    if (test.ContainsKey("encodedLength")) Equal(test["encodedLength"], encoded.Length, "编码字节数");
                    return PacketValue(encoded);
                case "storedFiles": // Windows 没有该历史入口，仅断言网络解析；Java 独立检查历史恢复。
                case "files": return FilesValue(Protocol.ParseFiles((string)input));
                case "filesRoundTrip":
                    var files = ((object[])input).Select(Map).Select(f => new Attachment { Id=(int)Number(f,"id"),Name=Text(f,"name"),Size=Number(f,"size"),Modified=Number(f,"modified"),Sha256=Text(f,"sha256"),RelativePath=Text(f,"relativePath") });
                    string wire = Protocol.FileList(files);
                    return new Dictionary<string,object> {{"wire",wire},{"files",FilesValue(Protocol.ParseFiles(wire))}};
                case "frame":
                    using (var stream = new MemoryStream()) {
                        SecureIdentity.WriteFrame(stream, Convert.FromBase64String((string)input));
                        string frame = Convert.ToBase64String(stream.ToArray()); stream.Position=0;
                        return new Dictionary<string,object> {{"wire",frame},{"payload",Convert.ToBase64String(SecureIdentity.ReadFrame(stream,(int)Number(test,"maximum")))}};
                    }
                case "readFrame":
                    using (var stream = new MemoryStream(Convert.FromBase64String((string)input))) return Convert.ToBase64String(SecureIdentity.ReadFrame(stream,(int)Number(test,"maximum")));
                default: throw new InvalidOperationException("未知向量操作：" + Text(test,"op"));
            }
        }
        private static void Case(Dictionary<string, object> test, object expected)
        {
            var error = expected as Dictionary<string, object>;
            if (error != null && error.ContainsKey("error")) {
                try { Execute(test); }
                catch (ArgumentException) { return; }
                catch (IOException) { return; }
                throw new Exception(Text(test,"name") + " 未拒绝非法输入");
            }
            Equal(expected, Execute(test), Text(test,"name"));
        }
        private static void Constants(Dictionary<string, object> constants)
        {
            Equal(constants["commands"], new Dictionary<string,object> {{"entry",Protocol.Entry},{"exit",Protocol.Exit},{"answer",Protocol.AnswerEntry},{"absence",Protocol.Absence},{"sendMessage",Protocol.SendMessage},{"receiveMessage",Protocol.ReceiveMessage},{"getFile",Protocol.GetFile},{"releaseFiles",Protocol.ReleaseFiles}}, "命令常量");
            Equal(constants["flags"],new Dictionary<string,object> {{"sendCheck",Protocol.SendCheck},{"fileAttach",Protocol.FileAttach},{"utf8",Protocol.Utf8},{"capUtf8",Protocol.CapUtf8}},"标志常量");
            Equal(constants["textAsFileUtf8Bytes"],MessageText.FileThreshold,"文本转附件阈值");
            Equal(constants["maxPacketBytes"],Protocol.MaxPacketBytes,"MaxPacketBytes");
            Equal(constants["maxHeaderColonOffset"],Protocol.MaxHeaderColonOffset,"MaxHeaderColonOffset");
            Equal(constants["maxHeaderFieldChars"],Protocol.MaxHeaderFieldChars,"MaxHeaderFieldChars");
            Equal(constants["maxFileNameUtf8Bytes"],Protocol.MaxFileNameUtf8Bytes,"MaxFileNameUtf8Bytes");
            Equal(constants["maxFileNameChars"],Protocol.MaxFileNameChars,"MaxFileNameChars");
            Equal(constants["maxRelativePathChars"],Protocol.MaxRelativePathChars,"MaxRelativePathChars");
            Equal(constants["maxRelativePathSegments"],Protocol.MaxRelativePathSegments,"MaxRelativePathSegments");
            Equal(constants["maxParsedAttachments"],Protocol.MaxParsedAttachments,"MaxParsedAttachments");
            Equal(constants["maxAvatarJpegBytes"],Protocol.MaxAvatarJpegBytes,"MaxAvatarJpegBytes");
            Equal(constants["maxAttachmentsPerMessage"],Protocol.MaxAttachmentsPerMessage,"MaxAttachmentsPerMessage");
            Equal(constants["helloFrameBytes"],Protocol.HelloFrameBytes,"HelloFrameBytes");
            Equal(Number(constants,"defaultChatPort"),(long)new AppSettings().Port,"默认端口不同");
            int hello=(int)Number(constants,"helloFrameBytes");
            foreach (int length in new[] {hello,hello+1}) {
                using(var frame=new MemoryStream()) {
                    SecureIdentity.WriteFrame(frame,new byte[length]); frame.Position=0;
                    try { Equal(hello,SecureIdentity.ReadFrame(frame,Protocol.HelloFrameBytes).Length,"Hello 边界"); }
                    catch(IOException) { if(length==hello)throw; continue; }
                    if(length!=hello)throw new Exception("超限 Hello 未拒绝");
                }
            }
            using(var output=new MemoryStream()) {
                int size=(int)Number(constants,"maxFrameBytes"); SecureIdentity.WriteFrame(output,new byte[size]);
                try { SecureIdentity.WriteFrame(output,new byte[size+1]); } catch(IOException) { return; }
                throw new Exception("最大帧未拒绝超限");
            }
        }
        internal static void RunAll()
        {
            DirectoryInfo directory=new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while(directory!=null&&!File.Exists(Path.Combine(directory.FullName,"tests/protocol-vectors.json")))directory=directory.Parent;
            if(directory==null)throw new FileNotFoundException("未找到共用协议向量文件 tests/protocol-vectors.json");
            var data=Map(Json.DeserializeObject(File.ReadAllText(Path.Combine(directory.FullName,"tests/protocol-vectors.json"),new UTF8Encoding(false,true))));
            Equal(2,data["version"],"向量版本"); Constants(Map(data["constants"]));
            int count=0;
            foreach(string group in new[]{"fieldCases","prefixCases","deviceIdAndPairCodeCases","avatarCases","fileNameAndRelativePathCases","packetCases","attachmentListCases","frameCases","wireValidationCases","historyCases"}) {
                object[] tests=(object[])data[group]; if(tests.Length==0)throw new Exception("向量组不能为空："+group);
                foreach(object entry in tests) {var test=Map(entry);Case(test,test["expected"]);count++;}
                Console.WriteLine("PASS protocol "+group+" "+tests.Length);
            }
            Console.WriteLine("PASS 共用协议向量 "+count+" 项 + 常量核对");
        }
        public static int Main() { try { RunAll();return 0; }catch(Exception e){Console.Error.WriteLine(e);return 1;} }
    }
}
