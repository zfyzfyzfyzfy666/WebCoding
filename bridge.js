'use strict';
// Dependency-free, single-folder MCP server. Authentication is the random URL token.
const http = require('node:http');
const fs = require('node:fs');
const fsp = fs.promises;
const path = require('node:path');
const crypto = require('node:crypto');
const {spawn, execFile} = require('node:child_process');
const exporting = process.argv.includes('--export-defaults');
const ROOT = fs.realpathSync(process.env.FL_ROOT || process.cwd());
const HOME = path.resolve(process.env.WC_HOME || __dirname);
const NATIVE = process.env.WC_NATIVE || path.join(HOME, 'bin', 'WebCoding.Native.exe');
const TOKEN = exporting ? '0'.repeat(48) : process.env.FL_TOKEN;
if (!TOKEN || !/^[a-f0-9]{48}$/.test(TOKEN)) throw new Error('Invalid startup token');
const endpoint = '/mcp/' + TOKEN;
const sessions = new Map();
const MAX_BODY = 8 * 1024 * 1024;
const limitRules = {
  command_timeout_ms: [60000,100,120000], max_output_chars: [200000,1000,1000000],
  max_file_bytes: [2097152,1024,16777216], max_processes: [4,1,16],
  process_log_chars: [200000,1000,1000000], search_max_results: [1000,10,5000],
  search_max_files: [10000,100,20000], image_max_edge: [1600,256,3840],
  image_max_bytes: [6291456,65536,8388608], agent_max_bytes: [32768,1024,131072]
};
const rawConfig = !exporting && fs.existsSync(path.join(HOME,'tools.json')) ? JSON.parse(fs.readFileSync(path.join(HOME,'tools.json'),'utf8').replace(/^\uFEFF/,'')) : {};
if (!rawConfig || typeof rawConfig !== 'object' || Array.isArray(rawConfig)) throw new Error('tools.json must be an object');
const limits = {};
for (const [key,[def,min,max]] of Object.entries(limitRules)) {
  const value = rawConfig.limits?.[key] ?? def;
  if (!Number.isInteger(value) || value < min || value > max) throw new Error('Invalid tools.json limit: '+key);
  limits[key] = value;
}
const MAX_FILE = limits.max_file_bytes;
const MAX_OUTPUT = limits.max_output_chars;
const activeChildren = new Set();
const isWin = process.platform === 'win32';
const norm = p => isWin ? p.toLowerCase() : p;
function inside(p) {
  const relative = path.relative(norm(ROOT), norm(p));
  return relative === '' || (relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative));
}
async function resolvePath(value = '.', writing = false) {
  if (typeof value !== 'string' || value.includes('\0')) throw new Error('Invalid path');
  const full = path.resolve(ROOT, value);
  if (!inside(full)) throw new Error('Path is outside the mapped folder');
  let ancestor = full;
  for (;;) {
    try {
      // lstat makes dangling symlinks fail closed instead of following them on write.
      await fsp.lstat(ancestor);
      const real = await fsp.realpath(ancestor);
      if (!inside(real)) throw new Error('Link points outside the mapped folder');
      return full;
    } catch (e) {
      if (!writing || e.code !== 'ENOENT') throw e;
      if (ancestor === ROOT) throw e;
      const parent = path.dirname(ancestor);
      if (parent === ancestor) throw e;
      // A dangling link itself is an existing entry and must not be traversed.
      try { const st = await fsp.lstat(ancestor); if (st.isSymbolicLink()) throw new Error('Dangling links are not writable'); }
      catch (err) { if (err.code !== 'ENOENT') throw err; }
      ancestor = parent;
    }
  }
}
function str(value, name) { if (typeof value !== 'string') throw new Error(name + ' must be a string'); return value; }
const text = value => ({content: [{type: 'text', text: value}]});
const object = (properties, required = []) => ({type: 'object', properties, required, additionalProperties: false});
const tools = [
  {name: 'list_directory', description: 'List files in the mapped folder; links outside it are not followed.', inputSchema: object({path: {type: 'string'}, recursive: {type: 'boolean'}}), annotations: {readOnlyHint: true}},
  {name: 'read_files', description: 'Read UTF-8 text files, optionally selecting a line range. Size limits are configurable in tools.json.', inputSchema: object({files: {type: 'array', minItems: 1, maxItems: 20, items: object({path: {type: 'string'}, offset: {type: 'integer', minimum: 1}, limit: {type: 'integer', minimum: 1, maximum: 10000}}, ['path'])}}, ['files']), annotations: {readOnlyHint: true}},
  {name: 'write_file', description: 'Create or overwrite a UTF-8 file inside the mapped folder.', inputSchema: object({path: {type: 'string'}, content: {type: 'string'}}, ['path', 'content']), annotations: {readOnlyHint: false, destructiveHint: true}},
  {name: 'edit_file', description: 'Replace exactly one occurrence of old_text. Missing or ambiguous matches fail without modifying the file.', inputSchema: object({path: {type: 'string'}, old_text: {type: 'string'}, new_text: {type: 'string'}}, ['path', 'old_text', 'new_text']), annotations: {readOnlyHint: false, destructiveHint: true}},
  {name: 'run_command', description: 'Run a PowerShell command with its working directory inside the mapped folder. NOT a security sandbox: commands have the current Windows user permissions and can access other locations.', inputSchema: object({command: {type: 'string'}, cwd: {type: 'string'}, timeout_ms: {type: 'integer', minimum: 100, maximum: 120000}}, ['command']), annotations: {readOnlyHint: false, destructiveHint: true, openWorldHint: true}}
];
tools.push(
  {name:'search_files',description:'Search filenames or UTF-8 text using literal matching. Supports basic glob filtering and snapshot pagination. Hidden and generated folders are skipped by default; returns explicit truncation/skip counts.',inputSchema:object({path:{type:'string'},pattern:{type:'string',maxLength:500},mode:{type:'string',enum:['name','content']},glob:{type:'string',maxLength:200},ignore_case:{type:'boolean'},include_hidden:{type:'boolean'},include_ignored:{type:'boolean'},page_size:{type:'integer',minimum:1,maximum:200},cursor:{type:'string'}},[]),annotations:{readOnlyHint:true}},
  {name:'manage_files',description:'Create a directory, move/rename without overwriting, or move an item into the project .webcoding-trash folder. Delete requires confirm_delete=true; root operations and permanent deletion are forbidden.',inputSchema:object({action:{type:'string',enum:['mkdir','move','rename','delete']},path:{type:'string'},destination:{type:'string'},confirm_delete:{type:'boolean'},recursive:{type:'boolean'}},['action','path']),annotations:{readOnlyHint:false,destructiveHint:true}},
  {name:'start_process',description:'Start a persistent foreground command in the mapped folder. Returns an owned process_id and initial logs. Use this for servers and watchers, not run_command. Child processes are stopped when the service exits.',inputSchema:object({command:{type:'string'},cwd:{type:'string'},startup_wait_ms:{type:'integer',minimum:0,maximum:10000}},['command']),annotations:{readOnlyHint:false,destructiveHint:true,openWorldHint:true}},
  {name:'read_process_output',description:'Read incremental output from an owned process_id using an absolute character cursor. Can wait for new output or process exit. Reports dropped old logs explicitly. Omit process_id to list this service\'s tasks.',inputSchema:object({process_id:{type:'string'},cursor:{type:'integer',minimum:0},max_chars:{type:'integer',minimum:100,maximum:100000},wait_for:{type:'string',enum:['output','exit']},wait_timeout_ms:{type:'integer',minimum:0,maximum:60000}},[]),annotations:{readOnlyHint:true}},
  {name:'stop_process',description:'Stop only a task created by this service, including its child process tree. Accepts an owned process_id, never an arbitrary system PID.',inputSchema:object({process_id:{type:'string'}},['process_id']),annotations:{readOnlyHint:false,destructiveHint:true}},
  {name:'read_image',description:'Read a PNG/JPEG/BMP/GIF file inside the mapped folder and return an MCP image block, resized if needed. No URL fetching. Requires a client that accepts image tool results.',inputSchema:object({path:{type:'string'},max_edge:{type:'integer',minimum:256,maximum:3840}},['path']),annotations:{readOnlyHint:true}},
  {name:'capture_window',description:'List windows or capture one selected by hwnd, PID, process name or title. Ambiguous matches return candidates. Does not activate/restore windows unless allow_activate=true. Screen capture requires explicit activation permission; protected/minimized/GPU windows may fail or be incomplete.',inputSchema:object({action:{type:'string',enum:['list','capture']},hwnd:{type:'string'},pid:{type:'integer',minimum:1},process_name:{type:'string'},title_contains:{type:'string'},allow_activate:{type:'boolean'},method:{type:'string',enum:['auto','printwindow','screen']},max_edge:{type:'integer',minimum:256,maximum:3840}},['action']),annotations:{readOnlyHint:false,destructiveHint:false,openWorldHint:true}}
);
if (exporting) {console.log(JSON.stringify({version:2,limits:Object.fromEntries(Object.entries(limitRules).map(([k,v])=>[k,v[0]])),tools:Object.fromEntries(tools.map(t=>[t.name,{enabled:true,description:t.description}]))},null,2));process.exit(0);}
if (rawConfig.tools && (typeof rawConfig.tools !== 'object' || Array.isArray(rawConfig.tools))) throw new Error('tools.json tools must be an object');
for (const name of Object.keys(rawConfig.tools || {})) if (!tools.some(t=>t.name===name)) throw new Error('Unknown configured tool: '+name);
for (const tool of tools) {
  const config = rawConfig.tools?.[tool.name] || {};
  if (config.enabled !== undefined && typeof config.enabled !== 'boolean') throw new Error('Invalid enabled flag: '+tool.name);
  if (config.description !== undefined && (typeof config.description !== 'string' || !config.description.trim() || config.description.length>4000)) throw new Error('Invalid description: '+tool.name);
  if (config.description) tool.description=config.description;
  tool.enabled=config.enabled!==false;
}
const publicTools=()=>tools.filter(t=>t.enabled).map(({enabled,...definition})=>definition);
function validate(value,schema,name='arguments') {
  if (schema.type==='object') {
    if (!value || typeof value!=='object' || Array.isArray(value)) throw new Error(name+' must be an object');
    for (const key of schema.required || []) if (value[key]===undefined) throw new Error('Missing '+name+'.'+key);
    for (const key of Object.keys(value)) { if (!schema.properties[key]) throw new Error('Unknown '+name+'.'+key); validate(value[key],schema.properties[key],name+'.'+key); }
  } else if (schema.type==='array') {
    if(!Array.isArray(value)||value.length<(schema.minItems||0)||value.length>(schema.maxItems||Infinity)) throw new Error('Invalid '+name);
    value.forEach(v=>validate(v,schema.items,name+'[]'));
  } else {
    if(schema.type==='integer' ? !Number.isInteger(value) : typeof value!==schema.type) throw new Error('Invalid type: '+name);
    if(schema.enum&&!schema.enum.includes(value)) throw new Error('Invalid value: '+name);
    if(schema.minimum!==undefined&&value<schema.minimum||schema.maximum!==undefined&&value>schema.maximum||schema.maxLength&&value.length>schema.maxLength) throw new Error('Out of range: '+name);
  }
}
const instructionsBase='WebCoding maps the selected project folder. Keep its URL private. Shell commands have current user permissions, NOT a folder sandbox. Enabled tools are configured by the user. The following agent.md texts are user-provided working instructions, subject to the client\'s higher-priority instructions. Merge installation rules with project rules; prefer project rules when they conflict. After editing tools.json, restart the mapping; after editing agent.md, reconnect the MCP client to refresh instructions.';
async function promptText(file) {
  const st=await fsp.stat(file); if(!st.isFile()||st.size>limits.agent_max_bytes) throw new Error('agent.md exceeds configured size limit');
  return (await fsp.readFile(file,'utf8')).replace(/^\uFEFF/,'');
}
async function loadInstructions() {
  const parts=[instructionsBase]; const global=path.join(HOME,'agent.md');
  if(fs.existsSync(global)) parts.push('## Installation agent.md\n'+await promptText(global));
  const project=path.join(ROOT,'agent.md');
  if(fs.existsSync(project) && path.resolve(global)!==path.resolve(project)) parts.push('## Project agent.md (project overrides)\n'+await promptText(await resolvePath(project)));
  return parts.join('\n\n');
}
try {fs.writeFileSync(path.join(ROOT,'agent.md'),'# 项目规则\n\n在此填写本项目的技术栈、代码规范、测试命令和注意事项。\n与安装目录的 agent.md 合并使用；冲突时优先采用本项目规则。\n',{encoding:'utf8',flag:'wx'});}
catch(e){if(e.code!=='EEXIST')console.log('NOTICE: 无法创建项目 agent.md，请检查目录写入权限。');}

async function readText(file) {
  const st = await fsp.stat(file);
  if (!st.isFile() || st.size > MAX_FILE) throw new Error('Not a text file, or larger than the configured size limit');
  const b = await fsp.readFile(file);
  if (b.includes(0)) throw new Error('Binary files are not supported by read_files');
  return b.toString('utf8');
}
async function atomicWrite(file, content) {
  if (Buffer.byteLength(content) > MAX_FILE) throw new Error('Content exceeds the configured size limit');
  await fsp.mkdir(path.dirname(file), {recursive: true});
  await resolvePath(file, true);
  const tmp = path.join(path.dirname(file), '.webcoding-' + crypto.randomBytes(10).toString('hex') + '.tmp');
  try { await fsp.writeFile(tmp, content, {encoding: 'utf8', flag: 'wx'}); await fsp.rename(tmp, file); }
  finally { await fsp.unlink(tmp).catch(() => {}); }
}
const jobs=new Map();
function spawnOwned(command,cwd) {
  const env={...process.env}; delete env.FL_TOKEN; delete env.FL_ROOT; delete env.WC_HOME; delete env.WC_NATIVE;
  if(isWin) {
    if(!fs.existsSync(NATIVE)) throw new Error('Native helper is missing; rerun the installer');
    env.WC_COMMAND=command;
    return spawn(NATIVE,['run'],{cwd,env,windowsHide:true,stdio:['ignore','pipe','pipe']});
  }
  return spawn('/bin/sh',['-c',command],{cwd,env,detached:true,stdio:['ignore','pipe','pipe']});
}
function terminateOwned(child) {
  if(child.exitCode!==null || child.signalCode) return;
  if(isWin) child.kill(); // The helper's own Windows job kills the entire command tree.
  else {try {process.kill(-child.pid,'SIGKILL');}catch{child.kill('SIGKILL');}}
}
async function commandDirectory(args) {
  const cwd=await resolvePath(args.cwd||'.');
  if(!(await fsp.stat(cwd)).isDirectory()) throw new Error('cwd is not a directory');
  if(!str(args.command,'command').trim()) throw new Error('Command cannot be empty');
  return cwd;
}
async function runCommand(args) {
  const cwd=await commandDirectory(args);
  const timeout=args.timeout_ms ?? limits.command_timeout_ms;
  return new Promise((resolve,reject)=>{
    let output='',truncated=false,timedOut=false,done=false,hardTimer;
    const child=spawnOwned(args.command,cwd);activeChildren.add(child);
    const append=chunk=>{const room=MAX_OUTPUT-output.length;if(chunk.length>room)truncated=true;if(room>0)output+=chunk.slice(0,room);};
    const finish=code=>{
      if(done)return;done=true;clearTimeout(timer);clearTimeout(hardTimer);activeChildren.delete(child);
      child.stdout.destroy();child.stderr.destroy();
      resolve({content:[{type:'text',text:'EXIT_CODE: '+code+(timedOut?'\nTIMEOUT: true':'')+'\nOUTPUT:\n'+output+(truncated?'\n[output truncated]':'')}],isError:timedOut||code!==0});
    };
    const timer=setTimeout(()=>{timedOut=true;terminateOwned(child);hardTimer=setTimeout(()=>finish(-1),3000);},timeout);
    child.stdout.setEncoding('utf8').on('data',append);child.stderr.setEncoding('utf8').on('data',append);
    child.on('error',e=>{clearTimeout(timer);activeChildren.delete(child);if(!done){done=true;reject(e);}});
    child.on('close',code=>finish(code===null?-1:code));
  });
}
function processView(job,cursor=job.base,max=20000) {
  if(cursor>job.total) throw new Error('cursor is beyond current output');
  const actual=Math.max(cursor,job.base), output=job.log.slice(actual-job.base,actual-job.base+max);
  return {process_id:job.id,pid:job.child.pid||null,running:!job.ended,exit_code:job.code,
    started_at:job.started,output,cursor:actual+output.length,dropped_chars:Math.max(0,job.base-cursor),has_more:actual+output.length<job.total};
}
function getJob(id) {const j=jobs.get(id);if(!j)throw new Error('Unknown process_id; only tasks from this mapping session are accessible');return j;}
function waitJob(job,mode,cursor,timeout) {
  if(job.ended || mode==='output'&&job.total>cursor || timeout===0)return Promise.resolve();
  return new Promise(resolve=>{
    let timer;
    const finish=()=>{clearTimeout(timer);job.listeners.delete(check);resolve();};
    const check=()=>{if(job.ended||mode==='output'&&job.total>cursor)finish();};
    job.listeners.add(check);timer=setTimeout(finish,timeout);check();
  });
}
async function startProcess(args) {
  if([...jobs.values()].filter(j=>!j.ended).length>=limits.max_processes)throw new Error('Persistent process limit reached');
  for(const [key,j] of jobs)if(j.ended&&Date.now()-j.finished>3600000)jobs.delete(key);
  if(jobs.size>=64){const old=[...jobs.values()].find(j=>j.ended);if(old)jobs.delete(old.id);}
  const cwd=await commandDirectory(args);
  // Re-check after the async filesystem validation to prevent simultaneous starts bypassing the cap.
  if([...jobs.values()].filter(j=>!j.ended).length>=limits.max_processes)throw new Error('Persistent process limit reached');
  const child=spawnOwned(args.command,cwd), id=crypto.randomUUID();
  const job={id,child,log:'',base:0,total:0,code:null,ended:false,started:new Date().toISOString(),listeners:new Set()};
  jobs.set(id,job);activeChildren.add(child);
  const notify=()=>{for(const fn of [...job.listeners])fn();};
  const append=chunk=>{
    job.log+=chunk;job.total+=chunk.length;
    if(job.log.length>limits.process_log_chars){const n=job.log.length-limits.process_log_chars;job.log=job.log.slice(n);job.base+=n;} notify();
  };
  child.stdout.setEncoding('utf8').on('data',append);child.stderr.setEncoding('utf8').on('data',append);
  child.on('error',e=>{append('Process error: '+e.message+'\n');job.ended=true;job.code=-1;job.finished=Date.now();activeChildren.delete(child);notify();});
  child.on('close',code=>{job.ended=true;job.code=code===null?-1:code;job.finished=Date.now();activeChildren.delete(child);notify();});
  await waitJob(job,'exit',0,args.startup_wait_ms??1000);
  return text(JSON.stringify(processView(job,0),null,2));
}
async function readProcess(args) {
  if(!args.process_id)return text(JSON.stringify({processes:[...jobs.values()].map(j=>({process_id:j.id,pid:j.child.pid||null,running:!j.ended,exit_code:j.code,started_at:j.started}))},null,2));
  const job=getJob(args.process_id), cursor=args.cursor??job.base;
  if(cursor>job.total)throw new Error('cursor is beyond current output');
  if(args.wait_for)await waitJob(job,args.wait_for,cursor,args.wait_timeout_ms??10000);
  return text(JSON.stringify(processView(job,cursor,args.max_chars??20000),null,2));
}
async function stopProcess(args) {
  const job=getJob(args.process_id);if(!job.ended)terminateOwned(job.child);
  await waitJob(job,'exit',0,5000);
  return text(JSON.stringify(processView(job),null,2));
}
const searches=new Map();
const ignoredDirs=new Set(['.git','node_modules','dist','build','.next','.cache','.webcoding-trash']);
function searchPage(entry,index,count) {
  const items=entry.results.slice(index,index+count),next=index+items.length;
  return {results:items,next_cursor:next<entry.results.length?entry.id+':'+next:null,
    total_collected:entry.results.length,scanned_files:entry.scanned,skipped_files:entry.skipped,truncated:entry.truncated,expires_in_seconds:Math.max(0,Math.floor((entry.expiry-Date.now())/1000))};
}
async function searchFiles(args) {
  const count=args.page_size??50;
  for(const [key,e] of searches)if(e.expiry<Date.now())searches.delete(key);
  if(args.cursor) {
    const match=/^([a-f0-9-]{36}):(\d+)$/.exec(args.cursor);
    const entry=match&&searches.get(match[1]);if(!entry)throw new Error('Invalid or expired search cursor');
    const index=Number(match[2]);if(index>entry.results.length)throw new Error('Cursor out of range');
    return text(JSON.stringify(searchPage(entry,index,count),null,2));
  }
  const pattern=str(args.pattern,'pattern');if(!pattern)throw new Error('pattern cannot be empty');
  const base=await resolvePath(args.path||'.');if(!(await fsp.stat(base)).isDirectory())throw new Error('Search path must be a directory');
  const entry={id:crypto.randomUUID(),results:[],scanned:0,skipped:0,truncated:false,expiry:Date.now()+300000};
  const fold=v=>args.ignore_case===false?v:v.toLocaleLowerCase(); const needle=fold(pattern);
  let filter=null;
  if(args.glob) {
    if((args.glob.match(/[?*]/g)||[]).length>20)throw new Error('Glob has too many wildcards');
    const escaped=args.glob.replace(/[.+^${}()|[\]\\]/g,'\\$&').replace(/\*\*\//g,'\u0001').replace(/\*\*/g,'\u0002').replace(/\*/g,'[^/]*').replace(/\?/g,'[^/]').replace(/\u0001/g,'(?:.*/)?').replace(/\u0002/g,'.*');
    filter=new RegExp('^'+escaped+'$',args.ignore_case===false?'':'i');
  }
  const deadline=Date.now()+15000;
  const full=()=>entry.results.length>=limits.search_max_results||entry.scanned>=limits.search_max_files||Date.now()>deadline;
  async function walk(dir,depth) {
    let list;try{list=await fsp.readdir(dir,{withFileTypes:true});}catch{entry.skipped++;return;}
    list.sort((a,b)=>a.name.localeCompare(b.name));
    for(const item of list) {
      if(full()){entry.truncated=true;return;}
      if(!args.include_hidden&&item.name.startsWith('.'))continue;
      const filename=path.join(dir,item.name),relative=path.relative(ROOT,filename).split(path.sep).join('/');
      if(item.isSymbolicLink()){entry.skipped++;continue;}
      if(item.isDirectory()) {
        if(!args.include_ignored&&ignoredDirs.has(item.name))continue;
        if(depth>=20){entry.skipped++;entry.truncated=true;continue;}
        try {await walk(await resolvePath(filename),depth+1);}catch{entry.skipped++;}
      } else if(item.isFile()) {
        entry.scanned++;
        const local=path.relative(base,filename).split(path.sep).join('/');
        if(filter&&!filter.test(local))continue;
        if(args.mode==='name') {if(fold(local).includes(needle))entry.results.push({path:relative});continue;}
        let content;try{content=await readText(await resolvePath(filename));}catch{entry.skipped++;continue;}
        const lines=content.split(/\r?\n/);
        for(let n=0;n<lines.length;n++) {
          const index=fold(lines[n]).indexOf(needle);
          if(index>=0){const from=Math.max(0,index-80);entry.results.push({path:relative,line:n+1,column:index+1,excerpt:lines[n].slice(from,from+320)});}
          if(full()){entry.truncated=true;return;}
        }
      }
    }
  }
  await walk(base,0);
  if(searches.size>=8)searches.delete(searches.keys().next().value);
  searches.set(entry.id,entry);
  return text(JSON.stringify(searchPage(entry,0,count),null,2));
}
async function manageFiles(args) {
  const file=await resolvePath(args.path,args.action==='mkdir');
  if(norm(file)===norm(ROOT))throw new Error('The mapped root cannot be modified');
  if(args.action==='mkdir'){await fsp.mkdir(file,{recursive:true});return text('Directory created.');}
  if(args.action==='move'||args.action==='rename') {
    const destination=await resolvePath(str(args.destination,'destination'),true);
    if(norm(destination)===norm(ROOT)||norm(destination).startsWith(norm(file)+path.sep))throw new Error('Invalid move destination');
    try{await fsp.lstat(destination);throw new Error('Destination already exists; no overwrite performed');}catch(e){if(e.code!=='ENOENT')throw e;}
    await fsp.mkdir(path.dirname(destination),{recursive:true});await resolvePath(destination,true);
    await fsp.rename(file,destination);return text('Moved without overwriting.');
  }
  if(args.confirm_delete!==true)throw new Error('Deletion requires confirm_delete=true');
  const stat=await fsp.lstat(file);
  if(stat.isDirectory()&&!args.recursive&&(await fsp.readdir(file)).length>0)throw new Error('Nonempty directory requires recursive=true');
  const trash=path.join(ROOT,'.webcoding-trash');
  if(norm(file)===norm(trash)||norm(file).startsWith(norm(trash)+path.sep))throw new Error('Trash items must be restored with move, not deleted recursively');
  await resolvePath(trash,true);await fsp.mkdir(trash,{recursive:true});await resolvePath(trash);
  const name=Date.now()+'-'+crypto.randomBytes(8).toString('hex')+'-'+path.basename(file),destination=path.join(trash,name);
  await fsp.rename(file,destination);
  return text(JSON.stringify({deleted:true,permanent:false,original_path:path.relative(ROOT,file),trash_path:path.relative(ROOT,destination),restore:'Use manage_files move from trash_path to original_path.'},null,2));
}
let nativeBusy=0;
async function nativeCall(action,args) {
  if(!isWin)throw new Error('Window/image native tools require Windows');
  if(!fs.existsSync(NATIVE))throw new Error('Native helper is missing; rerun the installer');
  if(nativeBusy>=2)throw new Error('Image/capture operation limit reached; retry after the active operation completes');
  nativeBusy++;
  try {
    return await new Promise((resolve,reject)=>{
      execFile(NATIVE,[action,Buffer.from(JSON.stringify(args)).toString('base64')],{windowsHide:true,timeout:15000,maxBuffer:16*1024*1024,encoding:'utf8'},(error,out)=>{
        let result;try{result=JSON.parse(out.replace(/^\uFEFF/,''));}catch{reject(new Error(error?'Native operation timed out or failed':'Invalid native response'));return;}
        if(!result.ok){reject(new Error(result.error||'Native operation failed'));return;}
        resolve(result);
      });
    });
  } finally {nativeBusy--;}
}
async function imageTool(name,args) {
  const request={...args,max_edge:Math.min(args.max_edge??limits.image_max_edge,limits.image_max_edge),max_bytes:limits.image_max_bytes};
  if(name==='read_image') {
    request.path=await resolvePath(args.path);
    if(!/\.(png|jpe?g|bmp|gif)$/i.test(request.path))throw new Error('Supported image formats: PNG, JPEG, BMP, GIF');
    if((await fsp.stat(request.path)).size>16*1024*1024)throw new Error('Image input exceeds 16 MB');
  }
  const result=await nativeCall(name==='read_image'?'image':'window',request);
  if(!result.image)return text(JSON.stringify(result,null,2));
  const {image,...metadata}=result;
  return {content:[{type:'text',text:JSON.stringify(metadata,null,2)},{type:'image',data:image,mimeType:'image/png'}]};
}

// Best-effort operational log, not a tamper-proof security audit. Never log file/image bodies.
const auditDirectory=path.join(HOME,'logs');
let auditSequence=0,auditFailed=false;
function redactLog(value) {
  let s=String(value??'');
  if(TOKEN)s=s.split(TOKEN).join('[REDACTED]');
  s=s.replace(/(\/mcp\/)[a-f0-9]{24,}/gi,'$1[REDACTED]')
    .replace(/(https?:\/\/)[^\s/@:]+:[^\s/@]+@/gi,'$1[REDACTED]@')
    .replace(/\b(Bearer|Basic)\s+[A-Za-z0-9._~+\/-]+=*/gi,'$1 [REDACTED]')
    .replace(/((?:["']?)(?:password|passwd|pwd|token|api[_-]?key|secret|authorization|access[_-]?key)(?:["']?)\s*[:=]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s;&,}]+)/gi,'$1[REDACTED]')
    .replace(/((?:--?|\/)\b(?:password|passwd|pwd|token|api[_-]?key|secret|authorization|access[_-]?key)(?:[= :]+))(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s;&,}]+)/gi,'$1[REDACTED]')
    .replace(/\b(?:sk-[a-zA-Z0-9_-]{12,}|ghp_[a-zA-Z0-9]{20,}|AKIA[A-Z0-9]{16})\b/g,'[REDACTED]');
  return s.replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g,'').replace(/\r?\n/g,' ↵ ');
}
function logValue(value,max=6000) {const s=redactLog(value);return s.length>max?s.slice(0,max)+' …[truncated]':s;}
function auditSummary(name,a) {
  const selected={};
  if(!a||typeof a!=='object'||Array.isArray(a))return selected;
  for(const k of ['path','cwd','action','destination','mode','glob','pattern','process_id','hwnd','pid','process_name','title_contains','timeout_ms','allow_activate','recursive','confirm_delete'])
    if(a[k]!==undefined)selected[k]=typeof a[k]==='string'?logValue(a[k],600):a[k];
  if(name==='run_command'||name==='start_process')selected.command=logValue(a.command);
  if(Array.isArray(a.files))selected.files=a.files.slice(0,20).map(f=>({path:logValue(f?.path,600),offset:f?.offset,limit:f?.limit}));
  if(typeof a.content==='string')selected.content_bytes=Buffer.byteLength(a.content);
  if(typeof a.old_text==='string')selected.old_text_bytes=Buffer.byteLength(a.old_text);
  if(typeof a.new_text==='string')selected.new_text_bytes=Buffer.byteLength(a.new_text);
  return selected;
}
function auditWrite(event) {
  const record={time:new Date().toISOString(),server_pid:process.pid,...event};
  const status={start:'开始',ok:'成功',error:'失败'}[event.phase]||event.phase;
  const detail=event.phase==='start'?JSON.stringify(event.arguments):[event.duration_ms+'ms',event.exit_code!==undefined?'exit='+event.exit_code:'',event.process_id?'process_id='+event.process_id:'',event.running!==undefined?'running='+event.running:''].filter(Boolean).join(' ');
  console.log('['+new Date().toLocaleTimeString('zh-CN',{hour12:false})+'] [工具 '+event.call_id+'] '+event.tool+' · '+status+' '+detail);
  try {
    fs.mkdirSync(auditDirectory,{recursive:true});const file=path.join(auditDirectory,'activity.jsonl');
    if(fs.existsSync(file)&&fs.statSync(file).size>=2*1024*1024) {
      const old=file+'.2',previous=file+'.1';if(fs.existsSync(old))fs.unlinkSync(old);if(fs.existsSync(previous))fs.renameSync(previous,old);fs.renameSync(file,previous);
    }
    fs.appendFileSync(file,JSON.stringify(record)+'\n','utf8');
  } catch {if(!auditFailed){auditFailed=true;console.error('[日志] 无法写入活动日志，继续在终端显示。');}}
}
async function auditedCall(name,args) {
  const call_id='A'+String(++auditSequence).padStart(6,'0'),started=Date.now(),safeName=logValue(name,80);
  // Definitions/arguments are validated before they are selected for logging, preventing arbitrary objects from leaking into logs.
  let definition;
  try {definition=tools.find(t=>t.name===name&&t.enabled);if(!definition)throw new Error('Tool is disabled or unknown');validate(args,definition.inputSchema);}
  catch(e){auditWrite({call_id,tool:safeName,phase:'error',duration_ms:Date.now()-started});throw e;}
  auditWrite({call_id,tool:safeName,phase:'start',arguments:auditSummary(name,args)});
  try {
    const response=await callTool(name,args),extra={};
    const first=response.content?.find(c=>c.type==='text')?.text||'';
    if(name==='run_command'){const match=/^EXIT_CODE: (-?\d+)/.exec(first);if(match)extra.exit_code=Number(match[1]);if(first.includes('\nTIMEOUT: true\n'))extra.timed_out=true;}
    if(['start_process','stop_process'].includes(name))try{const p=JSON.parse(first);extra.process_id=p.process_id;extra.running=p.running;extra.exit_code=p.exit_code;}catch{}
    auditWrite({call_id,tool:safeName,phase:response.isError?'error':'ok',duration_ms:Date.now()-started,...extra});return response;
  } catch(e){auditWrite({call_id,tool:safeName,phase:'error',duration_ms:Date.now()-started});throw e;}
}

async function callTool(name, args) {
  switch (name) {
    case 'list_directory': {
      const start = await resolvePath(args.path || '.');
      const rows = []; let limited = false;
      async function walk(dir, depth) {
        const entries = (await fsp.readdir(dir, {withFileTypes: true})).sort((a,b) => a.name.localeCompare(b.name));
        for (const item of entries) {
          if (rows.length >= 2000) {limited = true; return;}
          const full = path.join(dir, item.name), rel = path.relative(start, full);
          rows.push((item.isSymbolicLink() ? '[LINK] ' : item.isDirectory() ? '[DIR]  ' : '[FILE] ') + rel);
          if (args.recursive && item.isDirectory() && depth < 20) {
            try { await walk(await resolvePath(full), depth + 1); } catch { rows.push('[SKIPPED] ' + rel); }
          }
        }
      }
      await walk(start, 0);
      return text(rows.join('\n') + (limited ? '\n[limited to 2000 entries]' : ''));
    }
    case 'read_files': {
      if (!Array.isArray(args.files) || args.files.length < 1 || args.files.length > 20) throw new Error('files must contain 1–20 entries');
      const result = []; let total = 0;
      for (const item of args.files) {
        let content = await readText(await resolvePath(str(item.path, 'path')));
        if (item.offset !== undefined || item.limit !== undefined) {
          const offset = item.offset === undefined ? 1 : item.offset, limit = item.limit === undefined ? 1000 : item.limit;
          if (!Number.isInteger(offset) || offset < 1 || !Number.isInteger(limit) || limit < 1 || limit > 10000) throw new Error('Invalid line range');
          content = content.split(/\r?\n/).slice(offset - 1, offset - 1 + limit).join('\n');
        }
        total += Buffer.byteLength(content); if (total > 4 * 1024 * 1024) throw new Error('Combined read exceeds 4 MB');
        result.push('=== FILE: ' + item.path + ' ===\n' + content);
      }
      return text(result.join('\n\n'));
    }
    case 'write_file': await atomicWrite(await resolvePath(str(args.path, 'path'), true), str(args.content, 'content')); return text('File saved.');
    case 'edit_file': {
      const file = await resolvePath(str(args.path, 'path'));
      const oldText = str(args.old_text, 'old_text'), newText = str(args.new_text, 'new_text');
      if (!oldText) throw new Error('old_text cannot be empty');
      const content = await readText(file), at = content.indexOf(oldText);
      if (at < 0 || content.indexOf(oldText, at + 1) >= 0) throw new Error('old_text must match exactly once');
      await atomicWrite(file, content.slice(0, at) + newText + content.slice(at + oldText.length));
      return text('File updated.');
    }
    case 'run_command': return runCommand(args);
    case 'search_files': return searchFiles(args);
    case 'manage_files': return manageFiles(args);
    case 'start_process': return startProcess(args);
    case 'read_process_output': return readProcess(args);
    case 'stop_process': return stopProcess(args);
    case 'read_image': case 'capture_window': return imageTool(name,args);
    default: throw new Error('Unknown tool');
  }
}
function send(res, status, value, extra = {}) {
  const body = value === undefined ? '' : JSON.stringify(value);
  res.writeHead(status, {'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...extra});
  res.end(body);
}
function fail(res, id, code, message, status = 200) { send(res, status, {jsonrpc: '2.0', id: id === undefined ? null : id, error: {code, message}}); }
const server = http.createServer(async (req, res) => {
  const url = (req.url || '').split('?')[0];
  if (url === '/health/' + TOKEN && req.method === 'GET') return send(res, 200, {ok: true});
  if (url !== endpoint) return send(res, 404, {error: 'Not found'});
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Headers', 'content-type, accept, mcp-session-id, mcp-protocol-version');
  res.setHeader('Access-Control-Allow-Methods', 'POST, DELETE, OPTIONS');
  res.setHeader('Access-Control-Expose-Headers', 'Mcp-Session-Id');
  if (req.method === 'OPTIONS') return send(res, 204);
  let sid = req.headers['mcp-session-id'];
  if (sid && (!sessions.has(sid) || sessions.get(sid) < Date.now())) return fail(res, null, -32001, 'Session expired; initialize again', 404);
  if (sid) sessions.set(sid, Date.now() + 86400000);
  if (req.method === 'DELETE') {if (sid) sessions.delete(sid); return send(res, 204);}
  if (req.method !== 'POST') return send(res, 405, {message: 'Use an MCP client with HTTP POST.'}, {Allow: 'POST, DELETE, OPTIONS'});
  if (!/^application\/json(?:;|$)/i.test(req.headers['content-type'] || '')) return send(res, 415, {error: 'Expected application/json'});
  if (Number(req.headers['content-length']) > MAX_BODY) return send(res, 413, {error: 'Request too large'});
  let message;
  try {
    const parts = []; let length = 0;
    for await (const chunk of req) { length += chunk.length; if (length > MAX_BODY) {send(res, 413, {error: 'Request too large'}); return;} parts.push(chunk); }
    message = JSON.parse(Buffer.concat(parts).toString('utf8'));
  } catch { return fail(res, null, -32700, 'Invalid JSON', 400); }
  if (!message || message.jsonrpc !== '2.0' || typeof message.method !== 'string' || Array.isArray(message)) return fail(res, message && message.id, -32600, 'Invalid request', 400);
  if (message.id === undefined) return send(res, 202);
  const result = (value, headers) => send(res, 200, {jsonrpc: '2.0', id: message.id, result: value}, headers);
  switch (message.method) {
    case 'initialize': {
      if (sessions.size >= 1000) {for (const [key, expiry] of sessions) if (expiry < Date.now()) sessions.delete(key); if (sessions.size >= 1000) return fail(res, message.id, -32000, 'Session limit reached');}
      sid = crypto.randomUUID(); sessions.set(sid, Date.now() + 86400000);
      const requested = message.params && message.params.protocolVersion;
      const version = ['2024-11-05','2025-03-26','2025-06-18'].includes(requested) ? requested : '2025-03-26';
      try {return result({protocolVersion: version, capabilities: {tools: {}}, serverInfo: {name: 'WebCoding', version: '2.0.2'}, instructions: await loadInstructions()}, {'Mcp-Session-Id': sid});} catch(e) {return fail(res,message.id,-32000,e.message);}
    }
    case 'ping': return result({});
    case 'tools/list': return result({tools:publicTools()});
    case 'tools/call':
      try { const p=message.params||{}; return result(await auditedCall(p.name,p.arguments??{})); }
      catch (e) { return result({content: [{type: 'text', text: String(e.message || e)}], isError: true}); }
    default: return fail(res, message.id, -32601, 'Method not found');
  }
});
server.headersTimeout = 15000;
server.requestTimeout = 150000;
server.maxConnections = 64;
server.on('clientError', (_error, socket) => socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n'));
server.on('error', e => {console.error(e.message); process.exit(1);});
server.listen(0, '127.0.0.1', () => console.log('READY:' + server.address().port));
process.on('SIGTERM', () => {server.close(); for (const c of activeChildren) terminateOwned(c); process.exit(0);});
