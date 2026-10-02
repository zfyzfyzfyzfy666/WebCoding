using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace WebCoding {
// Concise connection-state log. Raw transport output is opt-in and never printed by default.
internal sealed class TunnelLog {
 readonly object gate=new object();readonly bool debug;readonly string directory;
 bool connected,everConnected,writeWarning,tcpWarning;int pendingErrors;DateTime lastError=DateTime.MinValue;
 public TunnelLog(bool diagnostic,string logDirectory){debug=diagnostic;directory=logDirectory;}
 public void OnLine(string line){if(string.IsNullOrWhiteSpace(line))return;lock(gate){
  if(debug)WriteDiagnostic(line);
  if(!tcpWarning && line.IndexOf("component=\"TCP Connectivity\"",StringComparison.OrdinalIgnoreCase)>=0 && line.IndexOf("status=fail",StringComparison.OrdinalIgnoreCase)>=0) {
   tcpWarning=true;Console.WriteLine("[连接] HTTP/2 的 TCP 连通性预检失败（端口 7844）；请检查代理、网络或防火墙。");
  }
  if(line.IndexOf("Registered tunnel connection",StringComparison.OrdinalIgnoreCase)>=0){
   if(!connected)Console.WriteLine("[连接] "+(everConnected?"隧道连接恢复":"隧道已连接")+(pendingErrors>0?"（已合并 "+pendingErrors+" 条连接错误）":""));
   connected=true;everConnected=true;pendingErrors=0;return;
  }
  if(line.IndexOf(" ERR ",StringComparison.OrdinalIgnoreCase)<0&&line.IndexOf(" WRN ",StringComparison.OrdinalIgnoreCase)<0)return;
  bool transport=Regex.IsMatch(line,"failed to dial|connection.*(?:terminated|closed)|no recent network activity|control stream|serve tunnel|failed to serve",RegexOptions.IgnoreCase);
  if(transport)connected=false;
  pendingErrors++;
  if((DateTime.UtcNow-lastError).TotalSeconds<60)return;
  string reason=Regex.IsMatch(line,"quic|no recent network activity",RegexOptions.IgnoreCase)?"QUIC 连接超时/中断":Regex.IsMatch(line,"resolve|dns|lookup",RegexOptions.IgnoreCase)?"域名解析异常":Regex.IsMatch(line,"x509|certificate verify|unknown authority|certificate expired",RegexOptions.IgnoreCase)?"证书校验失败":Regex.IsMatch(line,"tls|handshake",RegexOptions.IgnoreCase)?"TLS 连接异常":"连接或请求异常";
  Console.WriteLine("[连接] "+reason+"；"+(transport?"隧道正在自动重连":"请留意连接状态")+"。同类信息每分钟最多提示一次。"+(pendingErrors>1?"（累计 "+pendingErrors+" 条）":""));lastError=DateTime.UtcNow;
 }}
 void WriteDiagnostic(string line){try{
  Directory.CreateDirectory(directory);string file=Path.Combine(directory,"tunnel-debug.log");
  if(File.Exists(file)&&new FileInfo(file).Length>=2*1024*1024){if(File.Exists(file+".2"))File.Delete(file+".2");if(File.Exists(file+".1"))File.Move(file+".1",file+".2");File.Move(file,file+".1");}
  string safe=Regex.Replace(line,@"/mcp/[a-f0-9]{24,}","/mcp/[REDACTED]",RegexOptions.IgnoreCase);
  safe=Regex.Replace(safe,@"https://[a-z0-9-]+\.trycloudflare\.com","https://[private-tunnel]",RegexOptions.IgnoreCase);
  File.AppendAllText(file,DateTime.UtcNow.ToString("o")+" "+safe+Environment.NewLine,new UTF8Encoding(false));
 }catch{if(!writeWarning){writeWarning=true;Console.WriteLine("[日志] 无法写入隧道诊断文件。");}}}
}
}
