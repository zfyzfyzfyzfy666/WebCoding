using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly:System.Reflection.AssemblyTitle("WebCoding Configuration")]
[assembly:System.Reflection.AssemblyVersion("2.0.2.0")]
namespace WebCodingConfig {
static class Program {
 [STAThread] static int Main(string[] args) {
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  try {string home=args.Length>0?Path.GetFullPath(args[0]):Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
   using(var form=new Editor(home))return form.ShowDialog()==DialogResult.OK?0:2;
  }catch(Exception e){MessageBox.Show(e.Message,"WebCoding 配置错误",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
 }
}
sealed class Editor:Form {
 readonly string home; readonly TextBox prompt; readonly DataGridView tools,limits;
 readonly JavaScriptSerializer json=new JavaScriptSerializer(); Dictionary<string,object> config;
 Dictionary<string,object> toolConfig,limitConfig;
 readonly string[] keys={"command_timeout_ms","max_output_chars","max_file_bytes","max_processes","process_log_chars","search_max_results","search_max_files","image_max_edge","image_max_bytes","agent_max_bytes"};
 readonly int[] mins={100,1000,1024,1,1000,10,100,256,65536,1024};
 readonly int[] maxs={120000,1000000,16777216,16,1000000,5000,20000,3840,8388608,131072};
 public Editor(string directory) {
  home=directory;Text="WebCoding · 安装与工具配置";Size=new Size(1000,760);MinimumSize=new Size(760,580);StartPosition=FormStartPosition.CenterScreen;
  WebCodingBrand.Branding.Apply(this);
  Font=new Font("Microsoft YaHei UI",10);
  using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("WebCoding.ConfigDefaults"))
  using(var reader=new StreamReader(stream,Encoding.UTF8))config=json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
  Merge(config,json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(home,"tools.json"),Encoding.UTF8)));
  toolConfig=(Dictionary<string,object>)config["tools"];limitConfig=(Dictionary<string,object>)config["limits"];
  var tabs=new TabControl{Dock=DockStyle.Fill};
  var p1=new TabPage("安装 agent.md");prompt=new TextBox{Multiline=true,AcceptsTab=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Font=new Font("Consolas",11),Text=File.ReadAllText(Path.Combine(home,"agent.md"),Encoding.UTF8)};p1.Controls.Add(prompt);
  var p2=new TabPage("工具开关与描述");tools=Grid();tools.Columns.Add(new DataGridViewCheckBoxColumn{HeaderText="启用",Width=65});tools.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="工具名称",ReadOnly=true,Width=190});tools.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="发送给 Agent 的描述（可编辑）",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});
  foreach(var item in toolConfig){var v=(Dictionary<string,object>)item.Value;tools.Rows.Add(v.ContainsKey("enabled")?v["enabled"]:true,item.Key,v.ContainsKey("description")?v["description"]:item.Key);}p2.Controls.Add(tools);
  var p3=new TabPage("超时与输出限制");limits=Grid();limits.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="配置项",ReadOnly=true,Width=250});limits.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="值",Width=180});limits.Columns.Add(new DataGridViewTextBoxColumn{HeaderText="允许范围",ReadOnly=true,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill});
  for(int i=0;i<keys.Length;i++)limits.Rows.Add(keys[i],limitConfig[keys[i]],mins[i]+" – "+maxs[i]);p3.Controls.Add(limits);
  tabs.TabPages.AddRange(new[]{p1,p2,p3});
  var note=new Label{Dock=DockStyle.Top,Height=80,Padding=new Padding(15,10,10,5),Text="安装规则与项目根目录 agent.md 合并，冲突时项目规则优先。客户端决定如何采纳。\n工具配置修改后重启映射；规则修改后重新连接。关闭命令工具才可限制直接执行命令，但这不是安全沙箱。"};
  var bar=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=60,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(10)};
  var save=new Button{Text="保存并继续",Width=140,Height=35};save.Click+=(s,e)=>Save();var cancel=new Button{Text="取消",Width=100,Height=35,DialogResult=DialogResult.Cancel};bar.Controls.Add(save);bar.Controls.Add(cancel);
  Controls.Add(tabs);Controls.Add(note);Controls.Add(bar);CancelButton=cancel;
  var brandBar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=64,Padding=new Padding(15,6,10,6)};
  var logo=WebCodingBrand.Branding.Logo();logo.Size=new Size(48,48);brandBar.Controls.Add(logo);
  brandBar.Controls.Add(new Label{Text="WebCoding",AutoSize=true,Font=new Font("Segoe UI Semibold",16F),Margin=new Padding(10,8,0,0)});Controls.Add(brandBar);
 }
 static void Merge(Dictionary<string,object> target,Dictionary<string,object> source){foreach(var item in source){object current;if(target.TryGetValue(item.Key,out current)&&current is Dictionary<string,object>&&item.Value is Dictionary<string,object>)Merge((Dictionary<string,object>)current,(Dictionary<string,object>)item.Value);else target[item.Key]=item.Value;}}
 static DataGridView Grid(){return new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,BackgroundColor=Color.White,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells,DefaultCellStyle=new DataGridViewCellStyle{WrapMode=DataGridViewTriState.True}};}
 void Save(){try {
  tools.EndEdit();limits.EndEdit();
  foreach(DataGridViewRow row in tools.Rows){string name=Convert.ToString(row.Cells[1].Value),description=Convert.ToString(row.Cells[2].Value);if(string.IsNullOrWhiteSpace(description)||description.Length>4000)throw new Exception(name+" 的描述必须为 1–4000 个字符。");var v=(Dictionary<string,object>)toolConfig[name];v["enabled"]=Convert.ToBoolean(row.Cells[0].Value);v["description"]=description;}
  for(int i=0;i<keys.Length;i++){int n;if(!int.TryParse(Convert.ToString(limits.Rows[i].Cells[1].Value),out n)||n<mins[i]||n>maxs[i])throw new Exception(keys[i]+" 超出允许范围。");limitConfig[keys[i]]=n;}
  if(Encoding.UTF8.GetByteCount(prompt.Text)>Convert.ToInt32(limitConfig["agent_max_bytes"]))throw new Exception("agent.md 超过所设置的字节限制。");
  BackupWrite("tools.json",json.Serialize(config));BackupWrite("agent.md",prompt.Text);DialogResult=DialogResult.OK;Close();
 }catch(Exception e){MessageBox.Show(e.Message,"请检查配置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
 void BackupWrite(string name,string content){string file=Path.Combine(home,name),temp=file+".tmp-"+Guid.NewGuid().ToString("N");File.WriteAllText(temp,content,new UTF8Encoding(false));try{if(File.Exists(file))File.Replace(temp,file,file+".bak");else File.Move(temp,file);}finally{if(File.Exists(temp))File.Delete(temp);}}
}
}
