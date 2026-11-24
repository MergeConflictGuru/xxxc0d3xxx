import http from 'node:http';
import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {readFile, writeFile, mkdir, stat, unlink} from 'node:fs/promises';
import {randomBytes, randomUUID} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const runtime = process.env.CODEX_WEB_RUNTIME || path.resolve(root, '../.app_tmp/codex-web');
const publicRoot = path.join(root, 'public');
const token = randomBytes(32).toString('hex');
const sessions = new Set(), pending = new Map(), requests = new Map(), assets = new Map();
const activeTurns = new Set();
const routerRoot = process.env.CA_PERSISTENT_RECORD ? path.resolve(path.dirname(process.env.CA_PERSISTENT_RECORD),'../..') : null;
async function routerControl(method,params={}) {
  if(!routerRoot)throw new Error('This web instance is not connected to the account router');
  const record=JSON.parse(await readFile(process.env.CA_PERSISTENT_RECORD,'utf8'));
  const endpoint=JSON.parse((await readFile(record.controlEndpoint,'utf8')).replace(/^\uFEFF/,''));
  if(!Number.isInteger(endpoint.port)||endpoint.port<1||endpoint.port>65535)throw new Error('Invalid router endpoint');
  const bytes=JSON.stringify({method,params});
  return new Promise((resolve,reject)=>{
    const request=http.request({hostname:'127.0.0.1',port:endpoint.port,path:'/',method:'POST',headers:{'Content-Type':'application/json','X-CA-Token':record.controlToken},timeout:20000},response=>{
      let body='';response.setEncoding('utf8');response.on('data',data=>body+=data);response.on('end',()=>{try{const value=JSON.parse(body);if(value.error)reject(new Error(value.error.message||value.error));else resolve(value.result);}catch(error){reject(error);}});
    });request.on('error',reject);request.on('timeout',()=>request.destroy(new Error('Account router timed out')));request.end(bytes);
  });
}
let sequence = 0, eventId = 0, history = [], backend, ready, failure, initialized=false, recordPath;
const methods = new Set(['thread/list','thread/read','thread/turns/list','thread/items/list','thread/start','thread/resume','turn/start','turn/steer','turn/interrupt','model/list','account/read','account/rateLimits/read']);
const mime = {'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.js':'text/javascript; charset=utf-8','.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.webp':'image/webp','.gif':'image/gif','.avif':'image/avif'};
const send = value => backend.stdin.write(JSON.stringify(value)+'\n');
function publish(value) {
  const frame = `id: ${++eventId}\ndata: ${JSON.stringify(value)}\n\n`;
  history.push({id:eventId,frame}); if(history.length>2000) history.shift();
  for(const response of sessions) response.write(frame);
}
function rpc(method, params = {}) {
  if(failure) return Promise.reject(new Error(failure));
  return new Promise((resolve,reject)=>{
    const id = ++sequence;
    const timer = setTimeout(()=>{pending.delete(id);reject(new Error(`${method} timed out`));},120000);
    pending.set(id,{resolve,reject,timer});
    send({id,method,params});
  });
}
function failed(message) {
  failure = message;
  for(const p of pending.values()){clearTimeout(p.timer);p.reject(new Error(message));} pending.clear();
  publish({method:'web/disconnected',params:{message}});
}
function registerAsset(filename) {
  if(!path.isAbsolute(filename)) return null;
  for(const [id,entry] of assets) if(entry.path===filename)return `/api/asset/${id}`;
  const id=randomUUID();assets.set(id,{path:filename});return `/api/asset/${id}`;
}
function decorate(value) {
  if(!value || typeof value!=='object')return value;
  if(Array.isArray(value))return value.map(decorate);
  const copy=Object.fromEntries(Object.entries(value).map(([k,v])=>[k,decorate(v)]));
  if(['localImage','imageView','imageGeneration'].includes(value.type)) {
    const filename=value.savedPath||value.path;
    if(filename && mime[path.extname(filename).toLowerCase()]?.startsWith('image/')) copy.webAsset=registerAsset(filename);
  }
  if(value.type==='text'&&typeof value.text==='string') {
    const match=/^Attached file: ([^\n]+)\nLocal path: ([^\n]+)\nUse the appropriate file tools/.exec(value.text);
    if(match&&path.isAbsolute(match[2])) {
      const relative=path.relative(path.join(runtime,'uploads'),match[2]);
      if(relative&&!relative.startsWith('..')&&!path.isAbsolute(relative))copy.webFile={name:match[1],url:registerAsset(match[2])};
    }
  }
  return copy;
}
async function startBackend() {
  const command=process.env.CODEX_WEB_COMMAND ? JSON.parse(process.env.CODEX_WEB_COMMAND) : [process.env.CODEX_WEB_BINARY||'codex','app-server'];
  if(!Array.isArray(command)||!command.length||command.some(x=>typeof x!=='string'))throw new Error('CODEX_WEB_COMMAND must be a JSON array of command arguments');
  backend=spawn(command[0],command.slice(1),{cwd:process.env.CODEX_WEB_CWD||path.dirname(root),windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,GCM_INTERACTIVE:'never',GIT_TERMINAL_PROMPT:'0'}});
  backend.on('error',e=>failed(e.message)); backend.on('exit',(code)=>failed(`Codex server exited (${code}). Restart CodexWeb to reconnect.`));
  backend.stdin.on('error',e=>failed(e.message));
  backend.stderr.on('data',data=>process.stderr.write(data));
  createInterface({input:backend.stdout}).on('line',line=>{
    let value;try{value=JSON.parse(line);}catch{return;}
    if(value.method){
      if(value.method==='turn/started')activeTurns.add(value.params?.threadId);
      if(value.method==='turn/completed')activeTurns.delete(value.params?.threadId);
      value=decorate(value);
      if(Object.hasOwn(value,'id'))requests.set(String(value.id),value);
      publish(value);
    } else {
      const p=pending.get(value.id); if(!p)return;
      pending.delete(value.id);clearTimeout(p.timer);
      if(value.error)p.reject(Object.assign(new Error(value.error.message),{code:value.error.code}));else p.resolve(decorate(value.result));
    }
  });
  await rpc('initialize',{clientInfo:{name:'codex_web',title:'Codex Web',version:'1.0.0'},capabilities:{experimentalApi:true}});
  send({method:'initialized'});
  initialized=true;
}
async function body(request,limit=1024*1024) {
  const chunks=[];let size=0;
  for await(const chunk of request){size+=chunk.length;if(size>limit)throw Object.assign(new Error('Attachment/request too large (20 MB per file)'),{status:413});chunks.push(chunk);}
  return Buffer.concat(chunks);
}
function json(response,value,status=200){response.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});response.end(JSON.stringify(value));}
const cookieName=()=>`codex_web_${server.address().port}`;
function authorized(request){return request.headers.cookie?.split(';').some(x=>x.trim()===`${cookieName()}=${token}`);}
const server=http.createServer(async(request,response)=>{
  try {
    const host=request.headers.host;
    if(!host||!new Set([`127.0.0.1:${server.address().port}`,`localhost:${server.address().port}`]).has(host))return json(response,{error:'Invalid host'},403);
    if(request.headers.origin && ![`http://127.0.0.1:${server.address().port}`,`http://localhost:${server.address().port}`].includes(request.headers.origin))return json(response,{error:'Invalid origin'},403);
    response.setHeader('X-Content-Type-Options','nosniff');
    response.setHeader('Referrer-Policy','no-referrer');
    response.setHeader('Content-Security-Policy',"default-src 'self'; img-src 'self' data: blob: https:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'");
    const url=new URL(request.url,'http://localhost');
    if(url.pathname==='/'&&url.searchParams.get('key')===token){response.writeHead(303,{'Set-Cookie':`${cookieName()}=${token}; HttpOnly; SameSite=Strict; Path=/`,'Location':'/'});return response.end();}
    if(!authorized(request))return json(response,{error:'Open the launch URL printed in the terminal to connect.'},401);
    if(url.pathname==='/api/status')return json(response,{ready:initialized&&!failure,failure:failure||null,requests:[...requests.values()],cwd:process.env.CODEX_WEB_CWD||path.dirname(root),router:!!routerRoot,routerRoot,activeTurns:[...activeTurns]});
    if(url.pathname==='/api/router/state')return json(response,{result:await routerControl('state')});
    if(url.pathname==='/api/router/switch'&&request.method==='POST') {
      const {threadId,account}=JSON.parse(await body(request));
      if(!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(threadId||''))return json(response,{error:'Select a saved conversation first'},400);
      return json(response,{result:await routerControl('switch',{threadId,account})});
    }
    if(url.pathname==='/api/shutdown'&&request.method==='POST'){json(response,{stopping:true});setTimeout(shutdown,50);return;}
    if(url.pathname==='/api/events') {
      response.writeHead(200,{'Content-Type':'text/event-stream','Cache-Control':'no-cache','Connection':'keep-alive'});
      response.write(': connected\n\n');
      const last=Number(request.headers['last-event-id']);
      if(last) {if(history.length&&last<history[0].id-1)response.write(`data: ${JSON.stringify({method:'web/resync'})}\n\n`);else for(const item of history)if(item.id>last)response.write(item.frame);}
      sessions.add(response);const heartbeat=setInterval(()=>response.write(': keepalive\n\n'),15000);
      request.on('close',()=>{sessions.delete(response);clearInterval(heartbeat);});return;
    }
    if(url.pathname==='/api/rpc'&&request.method==='POST') {
      const {method,params}=JSON.parse(await body(request));
      if(!methods.has(method))return json(response,{error:'Unsupported method'},400);
      await ready; return json(response,{result:await rpc(method,params)});
    }
    if(url.pathname==='/api/reply'&&request.method==='POST') {
      const {id,result}=JSON.parse(await body(request));const original=requests.get(String(id));
      if(!original)return json(response,{error:'This request has already been answered'},409);
      send({id:original.id,result});requests.delete(String(id));publish({method:'web/requestAnswered',params:{id}});return json(response,{ok:true});
    }
    if(url.pathname==='/api/upload'&&request.method==='POST') {
      const name=path.basename(url.searchParams.get('name')||'attachment').replace(/[^\w. -]/g,'_').slice(-120);
      const bytes=await body(request,20*1024*1024);await mkdir(path.join(runtime,'uploads'),{recursive:true});
      const filename=path.join(runtime,'uploads',`${randomUUID()}-${name}`);await writeFile(filename,bytes,{flag:'wx'});
      return json(response,{name,path:filename,size:bytes.length,image:/^\.(png|jpe?g|webp|gif|avif)$/i.test(path.extname(name)),webAsset:registerAsset(filename)});
    }
    if(url.pathname.startsWith('/api/asset/')) {
      const entry=assets.get(url.pathname.split('/').at(-1));if(!entry)return json(response,{error:'Unknown asset'},404);
      const info=await stat(entry.path);if(info.size>32*1024*1024)return json(response,{error:'File too large to preview'},413);
      const contentType=mime[path.extname(entry.path).toLowerCase()]||'application/octet-stream';
      response.writeHead(200,{'Content-Type':contentType,'Content-Disposition':contentType.startsWith('image/')?'inline':'attachment','Cache-Control':'private, max-age=3600'});return response.end(await readFile(entry.path));
    }
    const files={'/':'index.html','/app.js':'app.js','/protocol.js':'protocol.js','/style.css':'style.css'};
    if(!files[url.pathname])return json(response,{error:'Not found'},404);
    response.writeHead(200,{'Content-Type':mime[path.extname(files[url.pathname])],'Cache-Control':'no-cache'});response.end(await readFile(path.join(publicRoot,files[url.pathname])));
  }catch(error){json(response,{error:error.message,code:error.code},error.status||500);}
});
await mkdir(runtime,{recursive:true});
ready=startBackend();ready.catch(error=>failed(error.message));
server.listen(Number(process.env.PORT||4783),'127.0.0.1',async()=>{
  const port=server.address().port,url=`http://127.0.0.1:${port}/?key=${token}`;
  recordPath=path.join(runtime,`server-${port}.json`);
  try{await writeFile(recordPath,JSON.stringify({pid:process.pid,url,port,routerRoot,cookie:`${cookieName()}=${token}`}));console.log(`Codex Web: ${url}`);}catch(error){console.error(error.message);shutdown();}
});
server.on('error',error=>{console.error(error.message);backend?.kill();process.exitCode=1;});
async function shutdown(){for(const r of sessions)r.end();backend?.stdin.end();backend?.kill();server.close();if(recordPath)await unlink(recordPath).catch(()=>{});setTimeout(()=>process.exit(),500);}
process.on('SIGINT',shutdown);process.on('SIGTERM',shutdown);
