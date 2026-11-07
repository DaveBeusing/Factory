'use strict';
const $ = id => document.getElementById(id);
const glyphs = {
 overview:'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
 folder:'<path d="M3 7V5a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
 target:'<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="5"/><circle cx="12" cy="12" r="1"/>',
 route:'<circle cx="6" cy="19" r="2"/><path d="M8 19h7a5 5 0 0 0 0-10H9a4 4 0 0 1 0-8h8"/><path d="m14 4 3-3 3 3"/>',
 layers:'<path d="m12 3 9 5-9 5-9-5zM3 12l9 5 9-5M3 16l9 5 9-5"/>',
 queue:'<path d="M8 6h13M8 12h13M8 18h13"/><path d="m3 6 1 1 2-2m-3 7 1 1 2-2m-3 7 1 1 2-2"/>',
 cpu:'<rect x="5" y="5" width="14" height="14" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 1v4m6-4v4M9 19v4m6-4v4M1 9h4m-4 6h4m14-6h4m-4 6h4"/>',
 activity:'<path d="M2 12h4l3-8 5 16 3-8h5"/>',gauge:'<path d="M4 19a9 9 0 1 1 16 0M12 12l4-4"/><circle cx="12" cy="12" r="1"/>',
 settings:'<circle cx="12" cy="12" r="3"/><path d="M19 13.5a7 7 0 0 0 0-3l2-1.5-2-3-2.4 1A7 7 0 0 0 14 5l-.5-2h-3L10 5a7 7 0 0 0-2.6 2l-2.4-1-2 3L5 10.5a7 7 0 0 0 0 3L3 15l2 3 2.4-1a7 7 0 0 0 2.6 2l.5 2h3l.5-2a7 7 0 0 0 2.6-2l2.4 1 2-3z"/>',
 search:'<circle cx="11" cy="11" r="7"/><path d="m16 16 5 5"/>',plus:'<path d="M12 4v16M4 12h16"/>',menu:'<path d="M4 6h16M4 12h16M4 18h16"/>',
 arrow:'<path d="m5 12 14 0m-6-6 6 6-6 6"/>',bolt:'<path d="m13 2-9 11h7l-1 9 10-12h-7z"/>',
 check:'<path d="m4 12 5 5L20 6"/>',clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
 spark:'<path d="m12 2 2.2 7.8L22 12l-7.8 2.2L12 22l-2.2-7.8L2 12l7.8-2.2z"/>',
 play:'<path d="m8 5 12 7-12 7z"/>',pause:'<rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/>',
 stop:'<rect x="5" y="5" width="14" height="14" rx="2"/>',refresh:'<path d="M20 11a8 8 0 1 0-2 6m2 3v-7h-7"/>',
 edit:'<path d="M4 20h4l12-12-4-4L4 16zM14 6l4 4"/>',shield:'<path d="M12 2 21 6v6c0 5-3 8-9 10C6 20 3 17 3 12V6z"/>',
 eye:'<path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/>',
 chevron:'<path d="m7 10 5 5 5-5"/>',bars:'<path d="M4 18V9m6 9V5m6 13v-6m4 6V3"/>',
 external:'<path d="M14 4h6v6m0-6-9 9M20 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h5"/>'
};
const ico = (name, cls='') => `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${glyphs[name]||glyphs.layers}</svg>`;
const esc = val => String(val ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const safeUrl = s => {try{const url=new URL(String(s));return url.protocol==='https:'?url.href:''}catch{return ''}};
const shorten = (s,n=55)=>String(s||'').length>n?String(s).slice(0,n-1)+'…':String(s||'');
const relative = date => { if(!date)return 'Never'; const ms=Date.now()-new Date(date).getTime();if(!Number.isFinite(ms))return 'Unknown';if(ms<0)return 'Just now';const s=Math.round(ms/1000);return s<60?`${s}s ago`:s<3600?`${Math.round(s/60)}m ago`:s<86400?`${Math.round(s/3600)}h ago`:`${Math.round(s/86400)}d ago`; };
const dateTime = s => s ? new Date(s).toLocaleString() : '—';
const badge = (s) => { const value=String(s||'unknown').replace(/_/g,' ');const tone=/^(active|online|integrated|passed|completed|completed local|pr ready|enabled|healthy)$/.test(value)?'success':/^(failed|blocked|stopped|disabled|offline|error)$/.test(value)?'danger':/^(paused|draining|awaiting approval|degraded|pending)$/.test(value)?'warning':'info';return `<span class="badge ${tone}">${esc(value)}</span>`; };
const readToken = () => {try{return sessionStorage.getItem('factory-token')||''}catch{return ''}};
let token=readToken(), state={projects:[],goals:[],jobs:[],workers:[],events:[],graph:[],knowledge:[],evaluations:[],releases:[],paused:false};
let planProject="";
const generatingPlans=new Set();
const number=value=>Number(value||0).toLocaleString();
let settings={}, usage=null, providerData=null, projectReview=null, analysisProject='', health=null, view='overview', search='', filter='all', streamController=null,lastHeartbeat=0, connected=false, lastStateUpdate=0, modalHandler=null, inspector=null, planDraft=null, planGoal=null;
let connectionRevision=0, loadingStateRevision=null;
const projectAccess=()=>token.startsWith('factory-viewer-');
let recoveryRequest=0, projectRecovery={projects:[],selected:'',loading:false};
async function api(path,method='GET',body){if(!token)throw new Error('Connect your API token first');if(projectAccess()&&!path.startsWith('viewer/'))throw new Error('This action requires an administrator connection');const res=await fetch('/api/'+path,{method,headers:{'Authorization':'Bearer '+token,...(body!==undefined?{'Content-Type':'application/json'}:{})},body:body===undefined?undefined:JSON.stringify(body),cache:'no-store'});if(!res.ok){let msg='Request failed ('+res.status+')';try{let obj=await res.json();msg=obj.error||obj.title||msg}catch{}const error=new Error(msg);error.status=res.status;throw error}if(res.status===204||res.status===202)return null;return res.json()}
const notify=(message,error=false)=>{const el=document.createElement('div');el.className='toast'+(error?' error':'');el.innerHTML=`<span class="toast-icon">${ico(error?'shield':'check')}</span><span>${esc(message)}</span>`;$('toast-stack').append(el);setTimeout(()=>el.remove(),5500)};
function condition(cls, val) { document.querySelectorAll(cls).forEach(el=>el.classList.toggle('hidden',!val)); }
function nav(v){view=v;filter='all';search='';document.querySelectorAll('.nav-item').forEach(b=>b.classList.toggle('active',b.dataset.view===v));$('breadcrumb').textContent=({plans:'Goal planner',analysis:'Project review'})[v]||v.charAt(0).toUpperCase()+v.slice(1);$('sidebar').classList.remove('open');$('sidebar-backdrop').classList.remove('open');render();$('main').focus({preventScroll:true});window.scrollTo({top:0,behavior:'instant'});}
function statusFromHealth(){
 const since=lastHeartbeat?Date.now()-lastHeartbeat:Infinity;
 const status=projectAccess()?(connected?'connected':'offline'):!token?'offline':!lastHeartbeat?(connected?'connecting':'offline'):connected&&since<8500?'connected':connected&&since<17000?'degraded':'offline';
 const label=status==='connected'?'Connected':status==='degraded'?'Connection delayed':status==='connecting'?'Connecting':'Offline';
 const brand=$('brand-connection'),dot=$('brand-connection-dot');
 dot.className='brand-connection-dot '+status;
 brand.title='Connection settings · '+label;
 brand.setAttribute('aria-label','Connection settings, '+label.toLowerCase());
 for(const id of ['sidebar-dot','footer-dot'])$(id).className='live-dot '+status;
 $('sidebar-status').textContent=status==='connected'?'System connected':status==='degraded'?'Connection delayed':status==='connecting'?'Connecting':'Connection offline';
 $('sidebar-heartbeat').textContent=lastHeartbeat?'Heartbeat '+relative(new Date(lastHeartbeat).toISOString()):'No heartbeat';
 $('footer-status').textContent=status==='connected'?(projectAccess()?'Project connection':'Realtime stream active'):status==='degraded'?'Reconnecting stream':status==='connecting'?'Connecting to event stream':'No active connection';
 $('status-clock').textContent=new Date().toLocaleTimeString();
}
function updateHealth(h){health=h;lastHeartbeat=Date.now();connected=true;statusFromHealth()}
async function startStream(){if(!token||projectAccess())return;if(streamController)streamController.abort();streamController=new AbortController();const controller=streamController,credential=token;
 while(!controller.signal.aborted){try{const res=await fetch('/api/events/stream',{headers:{Authorization:'Bearer '+credential},cache:'no-store',signal:controller.signal});if(!res.ok)throw Error('Stream '+res.status);
 const reader=res.body.getReader(),decoder=new TextDecoder();let buffer='';while(!controller.signal.aborted){const {done,value}=await reader.read();if(done||controller.signal.aborted)break;buffer+=decoder.decode(value,{stream:true});let pos;while(!controller.signal.aborted&&(pos=buffer.indexOf('\n\n'))>=0){const message=buffer.slice(0,pos);buffer=buffer.slice(pos+2);const data=message.split('\n').find(l=>l.startsWith('data: '));if(data){try{updateHealth(JSON.parse(data.slice(6)));if(Date.now()-lastStateUpdate>2200)await refreshState()}catch(e){console.warn('Heartbeat message:',e)}}}}
 }catch(e){if(controller.signal.aborted)break;connected=false;statusFromHealth()}await new Promise(r=>setTimeout(r,2800))}
}
async function refreshState(){
 const revision=connectionRevision;
 if(!token||projectAccess()||loadingStateRevision===revision)return;
 loadingStateRevision=revision;
 try{
  const snapshot=await api('state');
  if(revision!==connectionRevision)return;
  state=snapshot;lastStateUpdate=Date.now();connected=true;render();statusFromHealth();
 }catch{
  if(revision!==connectionRevision)return;
  connected=false;statusFromHealth();if(!lastStateUpdate)render();
 }finally{
  if(loadingStateRevision===revision)loadingStateRevision=null;
 }
}
async function refreshUsage(){if(!token||projectAccess())return;try{const u=await api('usage');usage=(!Number.isFinite(u?.weekly?.usedPercent)&&!Number.isFinite(u?.shortWindow?.usedPercent))?null:{...u,weekly:Number.isFinite(u?.weekly?.usedPercent)&&Number.isFinite(u?.weekly?.remainingPercent)?u.weekly:null,shortWindow:Number.isFinite(u?.shortWindow?.usedPercent)&&Number.isFinite(u?.shortWindow?.remainingPercent)?u.shortWindow:null}}catch{usage=null}condition('#usage-nav',!!usage||!!providerData);if(view==='usage'&&!usage&&!providerData)nav('overview');else render();refreshInspectorProviderCapacity()}
async function connect(nextToken){
 const revision=++connectionRevision;
 if(nextToken!==undefined){
  if(nextToken!==token){
   lastHeartbeat=0;
   connected=false;
   health=null;
   recoveryRequest++;
   projectRecovery={projects:[],selected:'',loading:false};
   streamController?.abort();
   closeInspector();
   $('inspector-body').replaceChildren();
   $('form-modal').close();
   $('modal-fields').replaceChildren();
   $('palette').close();
   modalHandler=null;
   $('toast-stack').replaceChildren();
  }
  token=nextToken;
  try{token?sessionStorage.setItem('factory-token',token):sessionStorage.removeItem('factory-token')}catch{}
 }
 if(!token){connected=false;if(streamController)streamController.abort();state={projects:[],goals:[],jobs:[],workers:[],events:[],graph:[],knowledge:[],evaluations:[],releases:[],paused:false};usage=null;render();statusFromHealth();return}
 if(projectAccess()){
  nav('recovery');
  await loadProjectRecovery();
  if(revision!==connectionRevision)return;
  if(connected)$('connection-modal').close();
  else{
   notify(projectRecovery.error||'Unable to connect with this project token.',true);
   if(!$('connection-modal').open)$('connection-modal').showModal();
  }
  return;
 }
 await refreshState();
 if(revision!==connectionRevision)return;
 if(!connected){
  notify('Invalid token or server unreachable',true);
  if(!$('connection-modal').open)$('connection-modal').showModal();
  return;
 }
 $('connection-modal').close();
 startStream();refreshUsage();fetchSettings();fetchManagedWorkers();fetchProviders();
}
async function fetchSettings(){try{settings=await api('settings')}catch{}}
async function fetchProviders(){try{providerData=await api('providers')}catch{providerData=null}condition('#usage-nav',!!usage||!!providerData);render();refreshInspectorProviderCapacity()}
async function fetchManagedWorkers(){try{managedWorkers=await api('workers/managed')}catch{managedWorkers=[]}}
async function mutate(path, method='POST',data){try{const result=await api(path,method,data);notify('Changes applied');await refreshState();if(path.includes('/plans/'))render();return result}catch(e){notify(e.message,true);throw e}}
function header(title,desc,action,actionLabel){return `<div class="page-head"><div><div class="eyebrow">MISSION CONTROL / ${esc(view.toUpperCase())}</div><h1>${esc(title)}</h1><p>${esc(desc)}</p></div><div class="head-actions">${action?`<button class="button button-primary" data-act="${esc(action)}">${ico('plus')}${esc(actionLabel||'Create new')}</button>`:''}</div></div>`}
function metric(label,value,foot,iconName){return `<div class="metric-card"><div class="metric-top">${esc(label)}${ico(iconName)}</div><div class="metric-value">${esc(value)}</div><div class="metric-foot">${esc(foot)}</div></div>`}
function card(title,desc,body,link,actions=''){return `<section class="card"><div class="card-head"><div><h2>${esc(title)}</h2><p>${esc(desc)}</p></div><div class="card-actions">${actions}${link?`<button class="subtle-link" data-view="${link}">View all ${ico('arrow')}</button>`:''}</div></div><div class="card-body">${body}</div></section>`}
function empty(iconName,title,desc,action,label){return `<div class="empty"><div class="empty-icon">${ico(iconName)}</div><h3>${esc(title)}</h3><p>${esc(desc)}</p>${action?`<button class="button button-muted" data-act="${action}">${ico('plus')}${esc(label||'Create new')}</button>`:''}</div>`}
function eventItems(events,count=8){return events.length?events.slice(-count).reverse().map(e=>`<div class="event-line"><div class="event-marker"></div><div><strong>${esc(e.type.replaceAll('_',' '))}</strong><small>${esc(relative(e.at))}${e.jobId?' · '+esc(e.jobId.slice(0,8)):''}</small><p>${esc(shorten(e.message,150))}</p></div></div>`).join(''):empty('activity','No activity yet','Factory events will appear here as work is performed.');}
function workerActive(w){return Date.now()-new Date(w.heartbeat).getTime()<20000}
function usageCards(){if(!usage)return '';const format=(w,label)=>{if(!w||!Number.isFinite(w.usedPercent)||!Number.isFinite(w.remainingPercent))return '';const used=Math.max(0,Math.min(100,w.usedPercent));return `<div class="usage-card"><div class="usage-top"><h3>${esc(label)}</h3>${badge(usage.stale?'cached':'available')}</div><div class="usage-percent">${w.remainingPercent.toFixed(1)}% <span>remaining</span></div><div class="progress-track" style="margin:13px 0 7px"><div class="progress-fill" style="width:${used.toFixed(2)}%"></div></div><div class="usage-values"><span>${used.toFixed(1)}% used</span><span>${w.remainingPercent.toFixed(1)}% free</span></div><div class="usage-note">${w.resetsAt?'Resets '+esc(dateTime(w.resetsAt)):'Reset time not reported'}${usage.observedAt?' · Updated '+esc(relative(usage.observedAt)):''}</div></div>`};return `<div class="usage-layout">${format(usage.weekly,'Weekly allowance')}${format(usage.shortWindow,'Short window')}</div>`}
function rowProjects(list){return list.map(p=>{const jobs=state.jobs.filter(j=>j.projectId===p.id);return `<tr><td><strong>${esc(p.name)}</strong><small class="mono">${esc(shorten(p.path,60))}</small></td><td>${badge(p.status||'active')}</td><td class="mono">${esc(p.baseBranch)}</td><td>${jobs.length}</td><td>${p.maxParallel}</td><td><div class="tiny-actions"><button class="tiny-button" data-inspect="project:${p.id}">Inspect</button><button class="tiny-button" data-edit="project:${p.id}">Configure</button></div></td></tr>`}).join('')}
function rowJobs(list){const pMap=Object.fromEntries(state.projects.map(p=>[p.id,p.name]));return list.map(j=>`<tr><td><strong>${esc(shorten(j.title,60))}</strong><small class="mono">${esc(j.id.slice(0,8))} · ${esc(j.mode)}</small></td><td>${esc(pMap[j.projectId]||'Unknown')}</td><td>${badge(j.state)}</td><td>${badge(j.risk)}</td><td>${j.priority}</td><td><div class="tiny-actions"><button class="tiny-button" data-inspect="job:${j.id}">Inspect</button><button class="tiny-button" data-edit="job:${j.id}" ${['queued','paused','stopped','failed','blocked'].includes(j.state)?'':'disabled'}>Edit</button></div></td></tr>`).join('')}
function table(columns,rows){return `<div class="table-wrap"><table><thead><tr>${columns.map(c=>`<th>${esc(c)}</th>`).join('')}</tr></thead><tbody>${rows}</tbody></table></div>`}
function renderOverview(){const active=state.jobs.filter(j=>['preparing','building','gating','reviewing','reworking','awaiting_approval'].includes(j.state));const blocked=state.jobs.filter(j=>['failed','blocked'].includes(j.state));const workersOnline=state.workers.filter(workerActive);const pipeline=state.jobs.filter(j=>j.state!=='integrated').slice(-5).reverse();let result=header('Good to see you.','Your engineering operations at a glance. Everything important, without the noise.');
 result+=`<div class="overview-hero"><div class="overview-hero-left"><div class="hero-orb">${ico('spark')}</div><div><h2>${state.paused?'Scheduling paused':'Operations are '+(blocked.length?'waiting for attention':'ready')}</h2><p>${active.length} running · ${blocked.length} need attention · ${state.jobs.filter(j=>j.state==='queued').length} in queue</p></div></div><div class="head-actions"><button class="button button-muted" data-view="queue">Manage queue</button><button class="button button-primary" data-act="queue-run">${ico('play')} Run queue</button></div></div>`;
 result+=`<div class="grid metrics-grid">${metric('Active projects',state.projects.filter(p=>(p.status||'active')==='active').length,'Configured repositories','folder')}${metric('Running jobs',active.length,'Execution & validation','activity')}${metric('Online workers',workersOnline.length+'/'+state.workers.length,'Heartbeat within 20s','cpu')}${metric('Needs attention',blocked.length,'Blocked or failed','shield')}</div>`;
 if(usage)result+=`<div style="margin-bottom:17px">${usageCards()}</div>`;
 const jobsBody=pipeline.length?table(['Job','Project','Stage','Risk','Priority','Actions'],rowJobs(pipeline)):empty('layers','No jobs yet','Create a job or generate a plan to begin execution.','create-job','Create job');
 const workerBody=state.workers.length?state.workers.slice(0,7).map(w=>`<div class="list-item"><span class="list-icon">${ico('cpu')}</span><span class="list-text"><strong>${esc(w.id)}</strong><small>${esc(w.platform)} · ${esc(relative(w.heartbeat))}</small></span><span class="list-end">${badge(!w.enabled?'disabled':w.draining?'draining':workerActive(w)?'online':'offline')}</span></div>`).join(''):empty('cpu','No registered workers','Register a worker to track its connection and operational state.','create-worker','Register worker');
 result+=`<div class="content-grid">${card('Job pipeline','Latest work packages and execution states',jobsBody,'jobs')}${card('Worker fleet','Heartbeat-verified connected workers',workerBody,'workers')}</div>`;
 const goalBody=state.goals.length?state.goals.slice(-5).reverse().map(g=>{const total=state.jobs.filter(j=>j.goalId===g.id);const done=total.filter(j=>j.integrated).length;return `<div class="list-item"><span class="list-icon">${ico('target')}</span><span class="list-text"><strong>${esc(shorten(g.objective,66))}</strong><small>${total.length} packages · ${done} integrated</small>${total.length?`<div class="progress-track" style="margin-top:8px"><div class="progress-fill" style="width:${Math.round(done/total.length*100)}%"></div></div>`:''}</span>${badge(g.state)}</div>`}).join(''):empty('target','No goals yet','A goal keeps plans and work packages aligned to an outcome.','create-goal','Set a goal');
 result+=`<div class="content-grid">${card('Current goals','Outcome-driven execution',goalBody,'goals')}${card('Recent activity','Live actions, reviews and changes',eventItems(state.events,7),'events')}</div>`;result+=`<section class="card" style="margin-top:17px"><div class="card-head"><div><h2>Operational intelligence</h2><p>Observed queue, review, integration and budget state</p></div><button class="subtle-link" data-view="recovery">Recovery center</button></div><div class="card-body">${renderOperationalPulse()}</div></section>`;return result}
function filtered(items,fields){return items.filter(x=>(filter==='all'||x.state===filter||x.status===filter)&&fields.some(k=>String(x[k]||'').toLowerCase().includes(search.toLowerCase())))}
function toolbar(viewName,statuses){return `<div class="toolbar"><div class="filters"><input class="filter-input" id="table-search" placeholder="Search ${esc(viewName)}..." value="${esc(search)}" aria-label="Search ${esc(viewName)}"><select class="filter-select" id="table-filter" aria-label="Filter by status"><option value="all">All statuses</option>${statuses.map(x=>`<option value="${x}" ${x===filter?'selected':''}>${esc(x)}</option>`).join('')}</select></div><small class="muted">${esc(viewName)} · live state</small></div>`}
function renderProjects(){const items=filtered(state.projects,['name','path','baseBranch']);return header('Projects','Register and configure repositories, quality gates and parallel execution limits.','create-project','Add project')+`<section class="card">${toolbar('projects',['active','paused','disabled','archived'])}${items.length?table(['Repository','State','Base branch','Jobs','Concurrency',''],rowProjects(items)):empty('folder','No matching projects','Register a repository to start planning and execution.','create-project','Add project')}</section>`}
function renderAnalysis(){
 if(!state.projects.length)return header('Project review','Inspect a registered repository to build evidence-backed draft goals.')+
  empty('folder','No registered projects','Register a repository before analyzing it.','create-project','Register project');
 const selected=state.projects.find(p=>p.id===analysisProject)||state.projects[0];
 const options=state.projects.map(p=>`<option value="${esc(p.id)}" ${p.id===selected.id?'selected':''}>${esc(p.name)}</option>`).join('');
 let body=header('Project review','Inspect source health, operational findings and quality gaps. Findings are advisory, not approvals.');
 body+=`<section class="card"><div class="card-body" style="padding-top:18px">
  <div class="field-row"><div class="field"><label for="analysis-project">Repository</label>
   <select id="analysis-project">${options}</select></div>
   <div style="display:flex;align-items:center;gap:10px"><button class="button button-primary" data-act="analysis-run" ${reviewingProjects.has(selected.id)?'disabled':''}>${ico('eye')} ${reviewingProjects.has(selected.id)?'Analyzing…':'Analyze project'}</button>
   <button class="button button-muted" data-act="project-plans" data-id="${selected.id}">Plan goals for this project</button></div></div>
  <p class="subtext">Read-only repository inspection; source commit is verified when converting findings to goals.</p>
 </div></section>`;
 if(projectReview&&projectReview.projectId===selected.id){
  body+=`<div class="grid metrics-grid" style="margin-top:17px">${metric('Findings',projectReview.findings.length,'Review before generating goals','shield')}
   ${metric('Indexed files',projectReview.indexedFiles,'Bounded static index','folder')}
   ${metric('Reference commit',shorten(projectReview.commit,11),'Captured master commit','route')}
   ${metric('Reviewed at',dateTime(projectReview.observedAt),'Snapshot time','clock')}</div>`;
  if(projectReview.advisory){
    body+=card('Provider advisory review',
      'Read-only model commentary, not verified test results or automatic approval.',
      `<p class="subtext">${esc(projectReview.advisoryProvider)} · ${esc(projectReview.advisoryModel||'Default model')}</p>
       <pre style="white-space:pre-wrap;overflow-wrap:anywhere">${esc(projectReview.advisory)}</pre>`);
  }
  const findings=projectReview.findings.map(f=>`<div class="entity-card"><div class="entity-top">
   <span class="entity-avatar">${ico('shield')}</span><div><div class="entity-name">${esc(f.title)}</div>
   <div class="entity-sub">${esc(f.category)}</div></div></div><p class="subtext">${esc(f.evidence)}</p>
   <p><strong>Proposed goal:</strong> ${esc(f.proposedGoal)}</p>
   <div class="entity-foot">${badge(f.severity)}
   <button class="button button-muted" data-act="analysis-goal" data-id="${esc(f.id)}">Review & create draft goal</button></div></div>`).join('');
  body+=`<section class="card" style="margin-top:17px"><div class="card-head"><div><h2>Review findings</h2><p>Approve individual proposals before creating goals; no work is executed here.</p></div></div>
   <div class="cards-list" style="padding:18px">${findings||empty('check','No findings detected','This limited static inspection cannot certify full code quality.')}</div></section>`;
 }else body+=`<section class="card" style="margin-top:17px">${empty('eye','No review loaded','Choose Analyze project to create a commit-bound review snapshot.')}</section>`;
 return body;
}
let reviewRequest=0;
const reviewingProjects=new Set();
async function loadProjectReview(id){
 analysisProject=id||analysisProject||state.projects[0]?.id||'';
 if(!analysisProject)return;
 const request=++reviewRequest, selected=analysisProject;
 if(projectReview?.projectId!==selected)projectReview=null;
 if(view==='analysis')render();
 try{
  const review=await api('projects/'+selected+'/analysis');
  if(request===reviewRequest)projectReview=review;
 }catch(error){
  if(request===reviewRequest&&error.message!=='No review exists yet')notify(error.message,true);
 }
 if(request===reviewRequest&&view==='analysis')render();
}
async function startProjectReview(){
 const id=analysisProject||state.projects[0]?.id;
 if(!id||reviewingProjects.has(id))return;
 reviewingProjects.add(id);render();
 try{
  const review=await api('projects/'+id+'/analysis','POST',{});
  if(analysisProject===id||!analysisProject){
   reviewRequest++;
   projectReview=review;
  }
  notify('Project review saved');
 }finally{reviewingProjects.delete(id);if(view==='analysis')render();await fetchProviders()}
}
function createReviewGoal(findingId){
 const review=projectReview;
 const finding=review?.findings?.find(f=>f.id===findingId);
 if(!finding)return;
 const body=field('objective','Goal outcome',finding.proposedGoal,'textarea')+
  field('criteria','Acceptance criteria (one per line)',finding.acceptanceCriteria.join('\n'),'textarea')+
  field('budget','Goal token budget',100000,'number');
 openForm('Goal from reviewed finding','Creating a draft goal does not approve execution. Review scope and acceptance criteria.',
  body,async form=>{
    const x=values(form);
    const goal=await mutate('projects/'+review.projectId+'/analysis/goals','POST',{
      reviewId:review.id,findingId,objective:x.objective,acceptanceCriteria:lines(x.criteria),tokenBudget:Number(x.budget)
    });
    planProject=review.projectId;
    nav('plans');await loadPlans();
    inspect('goal',goal.id);
  },'Create draft goal');
}
function renderGoals(){const items=filtered(state.goals,['objective']);const map=Object.fromEntries(state.projects.map(p=>[p.id,p.name]));return header('Goals','Define outcomes, acceptance criteria and token budgets.','create-goal','Create goal')+`<section class="card">${toolbar('goals',['draft','active','paused','stopped','completed','archived'])}<div class="cards-list" style="padding:18px">${items.length?items.map(g=>{const jobs=state.jobs.filter(j=>j.goalId===g.id);const done=jobs.filter(j=>j.integrated).length;return `<div class="entity-card"><div class="entity-top"><span class="entity-avatar">${ico('target')}</span><div><div class="entity-name">${esc(shorten(g.objective,85))}</div><div class="entity-sub">${esc(map[g.projectId]||'Unknown project')}</div></div></div><div class="entity-body">${g.acceptanceCriteria.length} acceptance criteria · ${g.tokenBudget.toLocaleString()} tokens<br>${jobs.length} work packages · ${done} integrated</div>${jobs.length?`<div class="progress-track"><div class="progress-fill" style="width:${Math.round(done/jobs.length*100)}%"></div></div>`:''}<div class="entity-foot">${badge(g.state)}<div class="tiny-actions"><button class="tiny-button" data-inspect="goal:${g.id}">Inspect</button><button class="tiny-button" data-edit="goal:${g.id}">Edit</button><button class="tiny-button" data-act="project-plans" data-id="${g.projectId}">Plan goals</button></div></div></div>`}).join(''):empty('target','No matching goals','Create a goal and set clear success criteria.','create-goal','Create goal')}</div></section>`}
function providerAccountAllowance(provider){
 if(provider.kind!=='codex'||(!usage?.weekly&&!usage?.shortWindow))return 'Unavailable';
 const windows=[['Weekly',usage.weekly],['Short window',usage.shortWindow]];
 return windows.filter(([,window])=>window).map(([label,window])=>{
  const reset=window.resetsAt?` · resets ${new Date(window.resetsAt).toLocaleString()}`:'';
  return `<small>${label}: ${number(window.remainingPercent)}% remaining${esc(reset)}</small>`;
 }).join('')+`<small>Shared controller Codex account${usage.stale?' · stale':''}</small>`;
}
function projectProviderCapacity(projectId){
 if(!providerData)return '<p class="subtext">Provider capacity is unavailable.</p>';
 const planner=providerData.assignments.find(a=>a.projectId===projectId&&a.role==='planner')?.providerId||'codex';
 const rows=providerData.usage.map(provider=>{
  const project=provider.projects.find(p=>p.projectId===projectId);
  if(!project)return '';
  const localCaps=[provider.localRemainingTokens,project.remainingTokens].filter(Number.isFinite);
  const remaining=localCaps.length?number(Math.min(...localCaps)):'No local cap';
  return `<tr><td>${esc(provider.id)}${provider.id===planner?'<small>Planner route</small>':''}${provider.enabled?'':'<small>Disabled</small>'}</td>
   <td>${number(project.inputTokens)}</td><td>${number(project.outputTokens)}</td><td>${number(project.reservedTokens)}</td><td>${remaining}</td><td>${providerAccountAllowance(provider)}</td></tr>`;
 }).join('');
 return `<h3>Project provider capacity</h3><p class="subtext">Local remaining uses the tighter project or profile ceiling. Account percentages describe the controller account and are separate from token budgets.</p>
 ${table(['Provider','Input','Output','Reserved','Local remaining','Account allowance'],rows)}`;
}
function refreshInspectorProviderCapacity(){
 const target=$('inspector-provider-capacity');
 if(target)target.innerHTML=projectProviderCapacity(target.dataset.project);
}
function renderPlans(){
 const projects=state.projects.map(p=>`<option value="${esc(p.id)}" ${p.id===planProject?'selected':''}>${esc(p.name)}</option>`).join('');
 const goals=state.goals.filter(g=>!planProject||g.projectId===planProject);
 const cards=goals.map(g=>{
  const plan=plansCache.get(g.id), busy=generatingPlans.has(g.id);
  const project=state.projects.find(p=>p.id===g.projectId);
  return `<div class="entity-card"><div class="entity-top"><span class="entity-avatar">${ico('route')}</span>
   <div><div class="entity-name">${esc(shorten(g.objective,85))}</div><div class="entity-sub">${esc(project?.name||'Unknown project')}</div></div></div>
   <div class="entity-body">${plan?`${plan.packages.length} work packages · master ${esc(shorten(plan.baseCommit,10))}`:'No saved plan'}<br>
   ${number(g.tokenBudget)} goal tokens · ${g.acceptanceCriteria.length} acceptance criteria</div>
   <div class="entity-foot">${badge(g.state)}<div class="tiny-actions">
    <button class="tiny-button" data-inspect="goal:${g.id}">Goal &amp; budget</button>
    <button class="tiny-button" data-act="plan-preflight" data-id="${g.id}">Check master &amp; open work</button>
    ${plan?`<button class="tiny-button" data-act="plan-view" data-id="${g.id}">Review plan</button>`:''}
    ${g.state==='draft'?`<button class="tiny-button" data-act="plan-edit" data-id="${g.id}" ${busy?'disabled':''}>${plan?'Edit plan':'Build plan'}</button>
    <button class="tiny-button" data-act="plan-generate" data-id="${g.id}" ${busy?'disabled':''}>${busy?'Generating…':plan?'Regenerate':'Generate'}</button>`:''}
    ${g.state==='active'?`<button class="tiny-button" data-act="queue-run">Run</button><button class="tiny-button" data-control="goal:${g.id}:pause">Pause</button><button class="tiny-button" data-control="goal:${g.id}:stop">Stop</button>`:''}
    ${['paused','stopped'].includes(g.state)?`<button class="tiny-button" data-control="goal:${g.id}:activate">Resume</button>`:''}
   </div></div></div>`;
 }).join('');
 return header('Goal planner','Review existing work, plan dependencies and inspect provider capacity.','create-goal','Create goal')+
 `<section class="card" style="margin-bottom:17px"><div class="card-body">
 <div class="field"><label for="plan-project">Project</label><select id="plan-project"><option value="">All projects</option>${projects}</select></div>
 ${planProject?`<button class="button button-muted" data-act="open-project-analysis" data-id="${esc(planProject)}">Project review</button>${projectProviderCapacity(planProject)}`:''}</div></section>`+
 `<div class="cards-list">${cards||empty('route','No goals in this view','Create a draft goal to plan its work packages.')}</div>`;
}
function renderJobs(){const items=filtered(state.jobs,['title','description','state','risk']);return header('Jobs','Inspect execution, manage priority and safely control work packages.','create-job','Create job')+`<section class="card">${toolbar('jobs',['queued','preparing','building','gating','reviewing','reworking','awaiting_approval','pr_ready','completed_local','integrated','paused','stopped','failed','blocked'])}${items.length?table(['Work package','Project','State','Risk','Priority',''],rowJobs(items.slice().reverse())):empty('layers','No matching jobs','Create a work package or adopt an approved plan.','create-job','Create job')}</section>`}
function renderWorkers(){const w=state.workers;return header('Workers','Monitor health and control remote-worker admission. Drain/disable never force-kills a running process.','create-worker','Register worker')+`<div class="grid metrics-grid">${metric('Registered',w.length,'Known workers','cpu')}${metric('Online',w.filter(workerActive).length,'Heartbeat < 20 seconds','activity')}${metric('Draining',w.filter(x=>x.draining).length,'No new work assigned','pause')}${metric('Available',w.filter(x=>x.enabled&&!x.draining&&workerActive(x)).length,'Eligible for claims','check')}</div><div class="cards-list">${w.length?w.map(x=>`<div class="entity-card"><div class="entity-top"><span class="entity-avatar">${ico('cpu')}</span><div><div class="entity-name">${esc(x.id)}</div><div class="entity-sub">${esc(x.platform)}</div></div></div><div class="entity-body"><div class="worker-heartbeat"><span class="live-dot ${workerActive(x)?'':'offline'}"></span>${workerActive(x)?'Live heartbeat':'Heartbeat unavailable'} · ${esc(relative(x.heartbeat))}</div><p>Admission: ${x.enabled?(x.draining?'Draining':'Enabled'):'Disabled'}</p></div><div class="entity-foot">${badge(!x.enabled?'disabled':x.draining?'draining':workerActive(x)?'online':'offline')}<div class="tiny-actions"><button class="tiny-button" data-inspect="worker:${encodeURIComponent(x.id)}">Inspect</button><button class="tiny-button" data-act="worker-edit" data-id="${encodeURIComponent(x.id)}">Edit</button><button class="tiny-button" data-control="worker:${encodeURIComponent(x.id)}:${managedWorkers.includes(x.id)?'stop':'start'}">${managedWorkers.includes(x.id)?'Stop local':'Start local'}</button><button class="tiny-button" data-control="worker:${encodeURIComponent(x.id)}:${x.draining||!x.enabled?'enable':'drain'}">${x.draining||!x.enabled?'Enable':'Drain'}</button><button class="tiny-button" data-control="worker:${encodeURIComponent(x.id)}:disable" ${!x.enabled?'disabled':''}>Disable</button></div></div></div>`).join(''):empty('cpu','No registered workers','Register a remote worker, then start it with the same ID to track its heartbeat.','create-worker','Register worker')}</div>`}
function renderQueue(){const queued=state.jobs.filter(j=>j.state==='queued');const active=state.jobs.filter(j=>['preparing','building','gating','reviewing','reworking','awaiting_approval'].includes(j.state));const waiting=queued.filter(j=>j.dependsOn.some(dep=>!state.jobs.some(o=>o.id===dep&&o.integrated)));return header('Queue & scheduling','Control admission, prioritize work and inspect dependency blockers.')+`<div class="overview-hero"><div class="overview-hero-left"><div class="hero-orb">${ico('queue')}</div><div><h2>${state.paused?'Queue paused':'Scheduler ready'}</h2><p>${queued.length} queued · ${active.length} active · ${waiting.length} waiting for dependencies</p></div></div><div class="queue-controls"><button class="button button-primary" data-act="queue-run">${ico('play')} Run</button><button class="button button-muted" data-act="queue-pause">${ico('pause')} Pause</button><button class="button button-muted" data-act="queue-resume">${ico('refresh')} Resume</button><button class="button button-danger" data-act="queue-stop">${ico('stop')} Stop</button></div></div><div class="grid metrics-grid">${metric('Ready or queued',queued.length,'Eligible after constraints','layers')}${metric('Active',active.length,'Working or validating','activity')}${metric('Waiting dependencies',waiting.length,'Must integrate predecessors','route')}${metric('Global concurrency',settings.maxParallel??'—','Configured worker slots','cpu')}</div>${card('Work schedule','Prioritized by eligibility and conflict',queued.length?table(['Work package','Project','State','Risk','Priority',''],rowJobs(queued.slice().sort((a,b)=>b.priority-a.priority))):empty('check','Queue is empty','No pending jobs. Add a job or adopt a plan.','create-job','Add job'))}<div style="margin-top:17px">${card('In flight','Active jobs continue during queue pause',active.length?table(['Work package','Project','State','Risk','Priority',''],rowJobs(active)):empty('activity','Nothing running','Start the queue when jobs are ready.'))}</div>`}
function renderEvents(){return header('Activity & audit trail','Recent persisted lifecycle events, decisions and operational changes.')+card('Event stream','Most recent events appear first',eventItems(state.events,250))}
function renderUsage(){
 const usd=new Intl.NumberFormat(undefined,{style:'currency',currency:'USD',maximumFractionDigits:6});
 const reportedCost=value=>Number.isFinite(value)?usd.format(value):'Unavailable';
 let body=header('Provider capacity','Per-provider and per-project Factory token usage and allocations. Account quotas are separate.');
 const rows=(providerData?.usage||[]).map(p=>
   `<tr><td><strong>${esc(p.id)}</strong><small>${esc(p.kind)} · ${esc(p.model||'Default model')}</small></td>
   <td>${Number(p.inputTokens||0).toLocaleString()}</td><td>${Number(p.outputTokens||0).toLocaleString()}</td>
   <td>${Number(p.reservedTokens||0).toLocaleString()}</td>
   <td>${p.localRemainingTokens===null?'Not configured':Number(p.localRemainingTokens).toLocaleString()}</td>
   <td>${reportedCost(p.reportedCostUsd)}</td>
   <td>${p.kind==='codex'&&usage?'See Codex account below':'Unavailable'}</td></tr>`).join('');
 body+=card('Provider and model consumption','Observed usage after provider attribution; unreported prior history cannot be reconstructed.',
   rows?table(['Provider','Input tokens','Output tokens','Held / active','Local remaining','Reported USD','Account allowance'],rows):
   empty('gauge','Provider data unavailable','Connect to the controller to load provider utilization.'));
 const modelRows=(providerData?.usage||[]).flatMap(p=>(p.models||[]).flatMap(m=>
  (m.roles||[]).map(role=>`<tr><td>${esc(p.id)}</td><td>${esc(m.model)}</td><td>${esc(role.role)}</td>
  <td>${Number(role.inputTokens||0).toLocaleString()}</td>
  <td>${Number(role.outputTokens||0).toLocaleString()}</td></tr>`))).join('');
 if(modelRows)body+=`<div style="margin-top:17px">`+
  card('Per-model and role consumption','Recorded provider token usage, grouped by model and agent responsibility.',
  table(['Provider','Model','Role','Input tokens','Output tokens'],modelRows))+`</div>`;
 const projects=(providerData?.usage||[]).flatMap(p=>(p.projects||[]).map(row=>
   `<tr><td>${esc(row.name)}</td><td>${esc(p.id)}</td><td>${Number(row.observedTokens||0).toLocaleString()}</td>
    <td>${Number(row.reservedTokens||0).toLocaleString()}</td>
    <td>${row.configuredLimit>0?Number(row.configuredLimit).toLocaleString():'Not configured'}</td>
    <td>${row.remainingTokens===null?'Unknown':Number(row.remainingTokens).toLocaleString()}</td>
    <td>${reportedCost(row.reportedCostUsd)}</td></tr>`)).join('');
 body+=`<div style="margin-top:17px">`+card('Project allocations','Local Factory ceilings are not provider-account entitlements.',
   projects?table(['Project','Provider','Consumed','Reserved','Configured limit','Local remaining','Reported USD'],projects):
   '<p class="subtext">No project allocation records.</p>')+`</div>`;
 const holds=(providerData?.heldReservations||[]).map(h=>
   `<tr><td>${esc(h.providerId)}</td><td>${esc(h.usage?.jobId||'Project operation')}${h.goalReservationId?`<small>Goal reservation ${esc(h.goalReservationId.slice(0,8))} · Generation ${esc(h.usage.executionGeneration)}</small>`:''}</td>
    <td>${Number(h.usage?.inputTokens||0).toLocaleString()} / ${Number(h.usage?.outputTokens||0).toLocaleString()}</td>
    <td>${number(Math.max(0,h.reserved-h.consumed))}</td>
    <td><button class="tiny-button" data-act="provider-reconcile" data-id="${esc(h.id)}">Reconcile</button></td></tr>`).join('');
 if(holds)body+=`<div style="margin-top:17px">`+
  card('Held provider reservations','Unknown final usage must be independently verified before releasing unused capacity.',
   table(['Provider','Job','Reported input / output','Held tokens',''],holds))+`</div>`;
 const foreign=(providerData?.foreignReservations||[]).map(r=>
  `<tr><td>${esc(r.providerId)}</td><td>${esc(r.ownerMachine)}</td>
   <td>${number(Math.max(0,r.reserved-r.consumed))}</td>
   <td><button class="tiny-button" data-act="provider-recover" data-id="${esc(r.id)}">Review stopped host</button></td></tr>`).join('');
 if(foreign)body+=`<div style="margin-top:17px">`+
  card('Reservations from another host','Verify the old runner has stopped before marking its reservations held.',
   table(['Provider','Recorded host','Reserved tokens outstanding',''],foreign))+`</div>`;
 if(usage)body+=`<div style="margin-top:17px">${usageCards()}</div>`;
 body+='<p class="subtext">Reported USD sums available provider estimates and may omit turns. Unknown provider account allowances are never estimated from local token use. Provider budgets are total local token ceilings, not rolling provider quotas.</p>';
 return body;
}
function renderSettings(){return header('Settings & policies','Review and configure execution limits and publication guardrails.')+`<div class="content-grid">${card('Runtime policy','Changes are stored in ~/.factory/settings.json',`<div class="job-info-grid">${[['Global concurrency',settings.maxParallel],['Agent sessions',settings.maxAgentSessions],['Review rounds',settings.maxReviewRounds],['Lease seconds',settings.leaseSeconds],['Push enabled',settings.allowPush?'Yes':'No'],['Auto-merge',settings.autoMerge?'Yes':'No']].map(([a,b])=>`<div class="kv"><small>${esc(a)}</small><strong>${esc(b)}</strong></div>`).join('')}</div><button class="button button-muted" style="margin-top:18px" data-act="edit-settings">${ico('edit')} Edit configuration</button>`)}${card('Operational safety','Safeguards for reliable execution',`<div class="list-stack">${[['shield','Human-controlled integration','Merging and publishing remain permission-gated'],['route','Dependency-aware execution','Jobs do not start before integrated dependencies'],['activity','Event history','Actions are recorded in persistent state'],['cpu','Worker isolation','Separate Git worktrees; not OS sandboxes']].map(([i,t,d])=>`<div class="list-item"><span class="list-icon">${ico(i)}</span><div class="list-text"><strong>${t}</strong><small>${d}</small></div></div>`).join('')}</div>`)}</div>`}

function recoveryCandidates(){
 const held=new Set((state.budgetReservations||[]).filter(r=>r.state==='held').map(r=>r.jobId));
 return state.jobs.filter(j=>['blocked','failed','paused','stopped'].includes(j.state)||held.has(j.id));
}
function recoveryExplanation(j){
 const hold=(state.budgetReservations||[]).some(r=>r.jobId===j.id&&r.state==='held');
 const git=['commit','push','pr','interrupted_git'].includes(j.stage);
 const lease=!!j.leaseOwner&&j.leaseUntil&&new Date(j.leaseUntil).getTime()>Date.now();
 const note=j.error||((j.state==='paused'||j.state==='stopped')?'Inspect the worktree and prior execution before resuming':'No failure detail reported');
 return {hold,git,lease,note};
}
async function loadProjectRecovery(selected=projectRecovery.selected){
 const request=++recoveryRequest;
 projectRecovery={projects:projectRecovery.projects,selected,loading:true};
 render();
 try{
  const projects=await api('viewer/projects');
  if(request!==recoveryRequest)return;
  connected=true;
  const project=projects.find(item=>item.id===selected)||projects[0];
  projectRecovery={projects,selected:project?.id||'',loading:true};
  if(project?.canReconcileBudget){
   const path='viewer/projects/'+encodeURIComponent(project.id);
   const [summary,goals,providers,receipts]=await Promise.all([
    api(path),api(path+'/goal-reservations'),api(path+'/provider-reservations'),api(path+'/executor-receipts')
   ]);
   if(request!==recoveryRequest)return;
   Object.assign(projectRecovery,{summary,goals,providers,receipts});
  }
 }catch(error){
  if(request!==recoveryRequest)return;
  if(error.status===401){connected=false;projectRecovery={projects:[],selected:''};}
  projectRecovery.error=error.status===403||error.status===404
   ?'Project access changed. Refresh to load your current grants.' : error.message;
 }finally{
  if(request===recoveryRequest){projectRecovery.loading=false;render();statusFromHealth();}
 }
}
function renderProjectRecovery(){
 const {projects,selected,loading,error,summary,goals,providers,receipts}=projectRecovery;
 const project=projects.find(item=>item.id===selected);
 const picker=projects.length?'<label for="recovery-project">Project</label> <select id="recovery-project">'+projects.map(item=>
  `<option value="${esc(item.id)}" ${item.id===selected?'selected':''}>${esc(item.name)}</option>`).join('')+'</select>':'';
 let result=header('Project recovery','Review held budgets and recover verified executor usage for a project.');
 result+=card('Project access','Recovery actions require the Budget Reconciler role for the selected project.',
  `<div class="field">${picker}</div>`,null,'<button class="button button-muted" data-act="refresh-project-recovery">Refresh projects</button>');
 if(loading)return result+'<p role="status">Loading project recovery…</p>';
 if(error)return result+`<p role="alert">${esc(error)}</p>`;
 if(!project)return result+empty('folder','No granted projects','Ask an administrator for project access.');
 if(!project.canReconcileBudget)return result+empty('shield','Budget Reconciler access required','Your grant for this project does not include budget recovery.');
 const goalRows=goals.map(hold=>{
  const goal=summary.goals.find(item=>item.id===hold.goalId);
  const job=summary.jobs.find(item=>item.id===hold.jobId);
  return `<tr><td>${esc(goal?.objective||hold.goalId)}</td><td>${esc(job?.title||hold.jobId)}</td>
   <td>${number(hold.reserved)}</td><td>${number(hold.consumed)}</td><td><button class="tiny-button"
   data-act="settle-project-goal" data-id="${esc(hold.id)}">Settle goal reservation</button></td></tr>`;
 }).join('');
 const receiptRows=receipts.map(hold=>{
  const job=summary.jobs.find(item=>item.id===hold.jobId);
  return `<tr><td>${esc(job?.title||hold.jobId)}</td><td>${esc(hold.providerId)}</td><td>${esc(hold.model)}</td>
   <td><button class="tiny-button" data-act="recover-project-receipt" data-id="${esc(hold.turnId)}">Recover executor receipt</button></td></tr>`;
 }).join('');
 const providerRows=providers.map(hold=>{
  const job=summary.jobs.find(item=>item.id===hold.jobId);
  const goal=summary.goals.find(item=>item.id===hold.goalId);
  const target=job?.title||hold.jobId||goal?.objective||hold.goalId||'Project usage';
  return `<tr><td>${esc(target)}<small class="mono">Hold ID: ${esc(hold.id)}</small></td><td>${esc(hold.providerId)}<small>${esc(hold.model||'Model unavailable')}</small></td>
   <td>${number(hold.reserved)}</td><td>${number(hold.inputTokens)} / ${number(hold.outputTokens)}</td>
   <td><button class="tiny-button" data-act="settle-project-provider" data-id="${esc(hold.id)}">Settle provider reservation</button></td></tr>`;
 }).join('');
 return result+`${card('Held provider reservations','Record independently verified final provider usage.',
  providerRows?table(['Target','Provider','Reserved','Observed input / output',''],providerRows):empty('check','No held provider reservations','No provider reservations require manual settlement.'))}
  <div class="content-grid">${card('Held goal reservations','Settle linked provider usage before closing a goal reservation.',
  goalRows?table(['Goal','Work package','Reserved','Consumed',''],goalRows):empty('check','No held goal reservations','No goal reservations require settlement.'))}
  ${card('Held executor turns','Recover final usage from the configured executor.',
  receiptRows?table(['Work package','Provider','Model',''],receiptRows):empty('check','No held executor turns','No executor receipts require recovery.'))}</div>`;
}
function formProjectRecovery(action,id){
 const context=projectRecovery;
 const project=context.projects.find(item=>item.id===context.selected&&item.canReconcileBudget);
 if(!project||context.loading||context.error)return;
 const goal=action==='settle-project-goal';
 const provider=action==='settle-project-provider';
 const holds=goal?context.goals:provider?context.providers:context.receipts;
 const hold=holds.find(item=>(goal||provider?item.id:item.turnId)===id);
 if(!hold)return;
 const path='viewer/projects/'+encodeURIComponent(project.id)+(goal
  ?'/goals/'+encodeURIComponent(hold.goalId)+'/budget/reconcile'
  :provider?'/provider-reservations/'+encodeURIComponent(hold.id)+'/reconcile'
  :'/jobs/'+encodeURIComponent(hold.jobId)+'/provider-turns/'+encodeURIComponent(hold.turnId)+'/recover');
 const job=context.summary.jobs.find(item=>item.id===hold.jobId);
 const linkedGoal=context.summary.goals.find(item=>item.id===hold.goalId);
 const target=job?.title||hold.jobId||linkedGoal?.objective||hold.goalId||'Project usage';
 const title=goal?'Settle goal reservation':provider?'Settle provider reservation':'Recover executor receipt';
 const tokens=goal||provider?field('input','Verified input tokens','','number')+field('output','Verified output tokens','','number'):'';
 const providerContext=provider?' · '+hold.providerId+(hold.model?' / '+hold.model:'')+' · Hold ID: '+hold.id:'';
 openForm(title,project.name+' · '+target+providerContext,tokens+
  field('reason','Evidence and review justification','','textarea')+
  field('reviewed','I independently reviewed the evidence',false,'checkbox')+
  field('confirmOverrun','I verified usage above the reserved tokens, if applicable',false,'checkbox'),
  async form=>{
   if(projectRecovery!==context)throw new Error('Project recovery changed. Review the selected project again.');
   const data=values(form), reason=data.reason.trim();
   if(!form.elements.reviewed.checked||reason.length<10||reason.length>1000)
    throw new Error('Confirm your review and enter an evidence reason of 10 to 1000 characters.');
   const body={reviewed:true,reason,confirmOverrun:form.elements.confirmOverrun.checked};
   if(goal||provider){
    const totals=[data.input,data.output].map(value=>value===''?NaN:Number(value));
    if(totals.some(value=>!Number.isSafeInteger(value)||value<0))
     throw new Error('Enter both verified token totals as nonnegative whole numbers.');
    if(goal)Object.assign(body,{reservationId:hold.id,finalTokensIn:totals[0],finalTokensOut:totals[1]});
    else Object.assign(body,{finalInputTokens:totals[0],finalOutputTokens:totals[1]});
   }
   try{await api(path,'POST',body);}
   catch(error){
    if(projectRecovery===context&&[401,403,404].includes(error.status)){
     $('form-modal').close();
     await loadProjectRecovery(project.id);
    }
    throw error;
   }
   if(projectRecovery===context){
    notify(goal?'Goal reservation settled':provider?'Provider reservation settled':'Executor receipt recovered');
    await loadProjectRecovery(project.id);
   }
  },title);
}
function renderRecovery(){
 const jobs=recoveryCandidates();
 const held=(state.budgetReservations||[]).filter(r=>r.state==='held');
 const interrupted=jobs.filter(j=>recoveryExplanation(j).git);
 let result=header('Recovery center','Investigate blocked and uncertain work before taking an explicit action.');
 result+=`<div class="grid metrics-grid">${metric('Needs review',jobs.length,'Failed, blocked or interrupted','shield')}${metric('Unsettled budgets',held.length,'Held reservations require reconciliation','gauge')}${metric('Git side-effect risk',interrupted.length,'Inspect external history before retry','route')}${metric('Expired active leases',state.jobs.filter(j=>j.leaseOwner&&j.leaseUntil&&new Date(j.leaseUntil).getTime()<=Date.now()).length,'Potentially uncertain execution','clock')}</div>`;
 const rows=jobs.map(j=>{
  const r=recoveryExplanation(j);
  return `<tr><td><strong>${esc(shorten(j.title,52))}</strong><small>${esc(j.id.slice(0,8))}</small></td><td>${badge(j.state)}<small>${esc(j.stage||'Unknown stage')}</small></td><td><span class="recovery-copy">${esc(shorten(r.note,160))}</span></td><td>${r.hold?badge('unsettled'):badge('no held reservation')}</td><td>${r.git?'Reconcile Git side effects':r.lease?'Execution may still be active':'Inspect checkpoint and process'}</td><td><button class="tiny-button" data-inspect="job:${j.id}">Investigate</button></td></tr>`;
 }).join('');
 result+=card('Recovery queue','No automatic replay of uncertain work',jobs.length?table(['Work package','State / stage','Observed reason','Budget','Safety check',''],rows):empty('check','No recovery cases','No blocked, failed, paused, stopped or unsettled jobs are currently reported.'));
 return result;
}
function renderOperationalPulse(){
 const queued=state.jobs.filter(j=>j.state==='queued');
 const waiting=queued.filter(j=>j.dependsOn.some(id=>!state.jobs.some(other=>other.id===id&&other.integrated)));
 const ages=queued.map(j=>Date.now()-new Date(j.createdAt).getTime()).filter(ms=>Number.isFinite(ms)&&ms>=0);
 const oldest=ages.length?Math.round(Math.max(...ages)/60000)+' min':'Unavailable';
 const reviews=state.jobs.filter(j=>j.state==='reviewing').length;
 const integration=state.jobs.filter(j=>['awaiting_approval','pr_ready','completed_local'].includes(j.state)).length;
 const constrained=state.jobs.filter(j=>j.stage==='budget'&&(j.state==='blocked'||JobStatesActiveUi.includes(j.state))).length;
 const active=state.jobs.filter(j=>JobStatesActiveUi.includes(j.state)).length;
 const enabledWorkers=state.workers.filter(w=>w.enabled);
 const connectedWorkers=enabledWorkers.filter(workerActive).length;
 const retries=state.jobs.filter(j=>Number(j.attempt)>1).length;
 return `<div class="operational-pulse">
 <div class="pulse-stat"><small>Active / pending jobs</small><strong>${active} / ${queued.length}</strong><span>Observed lifecycle states</span></div>
 <div class="pulse-stat"><small>Oldest queue wait</small><strong>${esc(oldest)}</strong><span>Elapsed since creation, not measured scheduling latency</span></div>
 <div class="pulse-stat"><small>Dependency bottlenecks</small><strong>${waiting.length}</strong><span>Unintegrated predecessors</span></div>
 <div class="pulse-stat"><small>Awaiting review / integration</small><strong>${reviews} / ${integration}</strong><span>Observed lifecycle states</span></div>
 <div class="pulse-stat"><small>Budget-blocked</small><strong>${constrained}</strong><span>Explicit budget stage</span></div>
 <div class="pulse-stat"><small>Registered worker connectivity</small><strong>${enabledWorkers.length?connectedWorkers+' / '+enabledWorkers.length:'Unavailable'}</strong><span>Heartbeat health; not total compute capacity</span></div>
 <div class="pulse-stat"><small>Jobs with retry activity</small><strong>${retries}</strong><span>More than one execution attempt</span></div></div>`;
}
const JobStatesActiveUi=['preparing','building','gating','reviewing','reworking','awaiting_approval'];

let renderedMain;
function render(){
 const scoped=projectAccess();
 document.body.classList.toggle('project-access',scoped);
 if(!token){renderedMain=null;$('main').innerHTML=`<div class="loading-screen"><div style="text-align:center">${ico('shield')}<h2>Connect to Mission Control</h2><p class="muted">Enter the API token to manage your Factory workspace.</p><button class="button button-primary" data-act="open-connection">Connect</button></div></div>`;return}
 if(scoped){
  const next=renderProjectRecovery();
  if(next!==renderedMain){
   const restoreFocus=document.activeElement===$('recovery-project');
   $('main').innerHTML=next;
   renderedMain=next;
   $('recovery-project')?.addEventListener('change',event=>loadProjectRecovery(event.target.value));
   if(restoreFocus)$('recovery-project')?.focus();
  }
  document.querySelectorAll('[data-count]').forEach(el=>el.textContent='');
  return;
 }
const renders={overview:renderOverview,projects:renderProjects,goals:renderGoals,analysis:renderAnalysis,plans:renderPlans,workers:renderWorkers,jobs:renderJobs,queue:renderQueue,recovery:renderRecovery,events:renderEvents,usage:renderUsage,settings:renderSettings};const next=(renders[view]||renderOverview)();
 const mainChanged=next!==renderedMain;
 if(mainChanged){$('main').innerHTML=next;renderedMain=next;}
 for(const name of ['projects','goals','jobs','workers'])document.querySelectorAll(`[data-count="${name}"]`).forEach(el=>el.textContent=(state[name]||[]).length||'');
  document.querySelectorAll('[data-count="recovery"]').forEach(el=>el.textContent=recoveryCandidates().length||'');
 if(!mainChanged)return;
 const projectFilter=$('plan-project');
 if(projectFilter)projectFilter.addEventListener('change',e=>{planProject=e.target.value;render();$('plan-project').focus()});
 const selector=$('analysis-project');
 if(selector)selector.addEventListener('change',e=>{analysisProject=e.target.value;loadProjectReview(analysisProject)});
 const s=$('table-search');if(s){s.addEventListener('input',e=>{const pos=e.target.selectionStart;search=e.target.value;render();$('table-search').focus();$('table-search').setSelectionRange(pos,pos)});$('table-filter').addEventListener('change',e=>{filter=e.target.value;render()})}}
// Dialogs, forms and commands ------------------------------------------------
const field=(key,label,value='',type='text',options=[],hint='')=>{let control='';if(type==='select')control=`<select name="${key}" id="field-${key}">${options.map(([v,l])=>`<option value="${esc(v)}" ${String(value)===String(v)?'selected':''}>${esc(l)}</option>`).join('')}</select>`;
else if(type==='textarea'||type==='code')control=`<textarea name="${key}" id="field-${key}" class="${type==='code'?'code-field':''}" rows="${type==='code'?7:4}">${esc(value)}</textarea>`;
else if(type==='checkbox')control=`<label class="checkbox-line"><input name="${key}" type="checkbox" ${value?'checked':''}> ${esc(label)}</label>`;
else control=`<input name="${key}" id="field-${key}" type="${type}" value="${esc(value)}" ${type==='number'?'min="0"':''}>`;
return type==='checkbox'?control:`<div class="field"><label for="field-${key}">${esc(label)}</label>${control}${hint?`<small>${esc(hint)}</small>`:''}</div>`};
const projectOptions=()=>state.projects.filter(p=>p.status!=='archived').map(p=>[p.id,p.name]);
const goalOptions=()=>[['','No linked goal'],...state.goals.filter(g=>g.state==='active').map(g=>[g.id,shorten(g.objective,70)])];
function openForm(title,subtitle,html,handler,submit='Save changes'){$('modal-title').textContent=title;$('modal-subtitle').textContent=subtitle;$('modal-fields').innerHTML=html;$('modal-submit').textContent=submit;modalHandler=handler;if(!$('form-modal').open)$('form-modal').showModal();}
function values(form){return Object.fromEntries([...new FormData(form).entries()].map(([k,v])=>[k,v.toString()]))}
const lines=s=>String(s||'').split(/\r?\n|;/).map(x=>x.trim()).filter(Boolean);
const checksJson=x=>JSON.stringify(x||[],null,2);
function formProject(id){const p=state.projects.find(p=>p.id===id),v=p||{name:'',path:'',baseBranch:'master',maxParallel:2,checks:[],protectedPaths:['.github/workflows/','.factory/','.agents/','factory/']};
 const html=field('name','Project name',v.name)+field('path','Git repository root',v.path)+field('baseBranch','Base branch',v.baseBranch)+field('maxParallel','Maximum parallel jobs',v.maxParallel,'number')+field('checks','Quality checks (JSON array)',checksJson(v.checks),'code',[],'Each check needs name, executable, arguments and timeoutSeconds.')+field('protectedPaths','Protected paths, one per line',v.protectedPaths.join('\n'),'textarea');
 openForm(p?'Configure project':'Register project','Quality gates are required before jobs can execute.',html,async f=>{const x=values(f);let checks;try{checks=JSON.parse(x.checks)}catch{throw Error('Invalid checks JSON')};if(!Array.isArray(checks))throw Error('Checks must be a JSON array');await mutate(p?`projects/${p.id}`:'projects',p?'PUT':'POST',{name:x.name,path:x.path,baseBranch:x.baseBranch,maxParallel:Number(x.maxParallel),checks,protectedPaths:lines(x.protectedPaths)})},p?'Update project':'Register project')}
function formProviderReconciliation(id){
 const reservation=(providerData?.heldReservations||[]).find(x=>x.id===id);
 if(!reservation)return;
 const html=field('input','Verified input tokens',reservation.usage?.inputTokens||0,'number')+
  field('output','Verified output tokens',reservation.usage?.outputTokens||0,'number')+
  field('reason','Independent evidence and justification','','textarea')+
  field('reviewed','I verified the final usage outside this dashboard',false,'checkbox')+
  field('confirmOverrun','I verified usage above the reserved tokens, if applicable',false,'checkbox');
 openForm('Reconcile provider usage','Only reconcile after confirming the provider reported final token totals.',
  html,async f=>{
    const x=values(f),input=Number(x.input),output=Number(x.output);
    if(!f.elements.reviewed.checked||x.reason.trim().length<10||
      !Number.isSafeInteger(input)||!Number.isSafeInteger(output)||input<0||output<0)
      throw Error('Verified token totals and an evidence reason are required');
    if(!confirm('Release the provider budget reservation after verified usage reconciliation?'))return;
    await mutate('providers/budget-reservations/'+id+'/reconcile','POST',
      {finalInputTokens:input,finalOutputTokens:output,reviewed:true,reason:x.reason.trim(),
        confirmOverrun:f.elements.confirmOverrun.checked});
    await fetchProviders();
  },'Reconcile usage');
}
function formProviderHostRecovery(id){
 const reservation=(providerData?.foreignReservations||[]).find(item=>item.id===id);
 if(!reservation)return;
 openForm('Recover reservation from another host','Verify the original runner and its provider process have stopped. Tokens stay reserved until usage is reconciled.',
  field('reviewed','I verified the original runner on '+reservation.ownerMachine+' has stopped',false,'checkbox')+
  field('reason','Shutdown evidence','','textarea'),async form=>{
   const reason=values(form).reason.trim();
   if(!form.elements.reviewed.checked||reason.length<10||reason.length>1000)
    throw Error('Confirm the original runner stopped and give an evidence reason of 10 to 1000 characters');
   await mutate('providers/budget-reservations/'+id+'/recover','POST',{reviewed:true,reason});
   await fetchProviders();
  },'Mark reservation held');
}
function formProjectProviders(id){
 const profiles=(providerData?.profiles||[]).filter(p=>p.enabled);
 if(!profiles.length){notify('No provider profiles available; configure ~/.factory/settings.json',true);return}
 const routes=Object.fromEntries((providerData.assignments||[]).filter(a=>a.projectId===id).map(a=>[a.role,a.providerId]));
 const budgets=Object.fromEntries((providerData.budgets||[]).filter(b=>b.projectId===id).map(b=>[b.providerId,b.tokenLimit]));
 const roleList=[['analysis','Project analysis'],['planner','Goal planning'],['builder','Implementation'],['review','Reviews (default)'],
  ['review:code','Code review'],['review:architecture','Architecture review'],
  ['review:security','Security review'],['review:ux','UX review']];
 let html='<h3>Role-based routing</h3><p class="subtext">Unset roles use Codex or the default review route. Builder providers must support workspace writes.</p>';
 for(let i=0;i<roleList.length;i++){
  const [role,label]=roleList[i];
  const options=[['','Default (Codex or inherited review route)'],...profiles
   .filter(p=>role!=='builder'||p.kind==='codex').map(p=>[p.id,p.id+' · '+(p.model||'Default model')])];
  html+=field('route_'+i,label,routes[role]||'','select',options);
 }
 html+='<h3>Project token ceilings</h3><p class="subtext">Zero removes the project ceiling. Provider account allowance remains unknown unless explicitly reported.</p>';
 for(let i=0;i<profiles.length;i++)html+=field('budget_'+i,profiles[i].id+' · total tokens',budgets[profiles[i].id]||0,'number');
 openForm('Project AI providers','Assignments apply to subsequent planning and jobs. Running jobs must finish first.',
  html,async form=>{
   const x=values(form),nextRoutes={},nextBudgets={};
   for(let i=0;i<roleList.length;i++)if(x['route_'+i])nextRoutes[roleList[i][0]]=x['route_'+i];
   for(let i=0;i<profiles.length;i++){
    const number=Number(x['budget_'+i]);
    if(!Number.isSafeInteger(number)||number<0)throw Error('Invalid token ceiling');
    nextBudgets[profiles[i].id]=number;
   }
   await mutate('projects/'+id+'/providers','PUT',{routes:nextRoutes,budgets:nextBudgets});
   await fetchProviders();
  },'Save provider routing');
}
function formGoal(id){const g=state.goals.find(g=>g.id===id);if(!g&&!state.projects.length){notify('Register a project first',true);return}let v=g||{projectId:state.projects[0].id,objective:'',acceptanceCriteria:[],tokenBudget:100000};const html=field('projectId','Project',v.projectId,'select',projectOptions())+field('objective','Outcome / objective',v.objective,'textarea')+field('acceptanceCriteria','Acceptance criteria (one per line)',v.acceptanceCriteria.join('\n'),'textarea')+field('tokenBudget','Token budget',v.tokenBudget,'number');openForm(g?'Edit goal':'Create goal','Describe a measurable outcome and its success criteria.',html,async f=>{let x=values(f);await mutate(g?`goals/${g.id}`:'goals',g?'PUT':'POST',{projectId:x.projectId,objective:x.objective,acceptanceCriteria:lines(x.acceptanceCriteria),tokenBudget:Number(x.tokenBudget)})},g?'Update goal':'Create goal')}
function formJob(id){const j=state.jobs.find(j=>j.id===id);if(!j&&!state.projects.length){notify('Register a project first',true);return}const v=j||{projectId:state.projects[0].id,goalId:'',title:'',description:'',mode:'local',risk:'medium',priority:50,dependsOn:[],expectedPaths:[]};let html=field('projectId','Project',v.projectId,'select',projectOptions())+field('goalId','Linked goal (optional)',v.goalId||'','select',goalOptions())+field('title','Title',v.title)+field('description','Work package description',v.description,'textarea')+`<div class="field-row">${field('mode','Execution',v.mode,'select',[['local','Local'],['remote','Remote']])}${field('risk','Risk',v.risk,'select',[['low','Low'],['medium','Medium'],['high','High']])}</div>`+field('priority','Priority (0-100)',v.priority,'number')+field('expectedPaths','Expected paths (one per line)',v.expectedPaths.join('\n'),'textarea')+field('dependsOn','Dependencies (job IDs, one per line)',v.dependsOn.join('\n'),'textarea')
 +field('reviewedExistingWork','I reviewed existing master code and overlapping work before creating this package',false,'checkbox');
 openForm(j?'Edit job':'Create job','Execution starts only when dependencies and policies allow it.',html,async f=>{const x=values(f);await mutate(j?`jobs/${j.id}`:'jobs',j?'PUT':'POST',{projectId:x.projectId,goalId:x.goalId||null,title:x.title,description:x.description,mode:x.mode,risk:x.risk,priority:Number(x.priority),expectedPaths:lines(x.expectedPaths),dependsOn:lines(x.dependsOn),
 reviewedExistingWork:!!f.elements.reviewedExistingWork?.checked})},j?'Save job':'Queue job')}
function formWorker(id){const w=state.workers.find(w=>w.id===id);openForm(w?'Configure worker':'Register worker','Remote workers authenticate with the Factory token. Local workers may be launched directly from this dashboard.',field('id','Unique worker ID',w?.id||'')+field('platform','Platform / capabilities',w?.platform||'remote'),async f=>{const x=values(f);if(w&&x.id!==w.id)throw Error('Worker ID cannot be changed');await mutate('workers/register','POST',x)},w?'Save worker':'Register worker')}
function formSettings(){
 const supportsExtensions=Number.isFinite(settings.goalExtensionIncrementTokens)&&
   Number.isFinite(settings.maxJobReservationTokens);
 let html=`<div class="field-row">${field('maxParallel','Global concurrency',settings.maxParallel||3,'number')}
 ${field('maxAgentSessions','Agent sessions',settings.maxAgentSessions||6,'number')}
 ${field('maxReviewRounds','Review rounds',settings.maxReviewRounds||3,'number')}
 ${field('leaseSeconds','Lease duration (s)',settings.leaseSeconds||120,'number')}
 ${field('pollSeconds','Polling interval (s)',settings.pollSeconds||3,'number')}</div>`;
 if(Number.isFinite(settings.goalReservationTokens))
  html+=field('goalReservationTokens','Initial goal reservation (tokens)',
   settings.goalReservationTokens,'number',[],'Initial tokens committed when a goal-funded job is claimed.');
 if(supportsExtensions)
  html+=`<h3>Dynamic goal allocations</h3><div class="field-row">
   ${field('goalExtensionIncrementTokens','Extension increment (tokens)',settings.goalExtensionIncrementTokens,'number')}
   ${field('goalExtensionThresholdTokens','Low-water threshold (tokens)',settings.goalExtensionThresholdTokens,'number')}
   ${field('maxJobReservationTokens','Maximum per-job allocation (tokens)',settings.maxJobReservationTokens,'number')}
   </div><p class="subtext">Controller-approved goal reservations, not provider account allowances.</p>`;
 html+=field('allowPush','Allow remote pushes',settings.allowPush,'checkbox')+
  field('autoMerge','Allow automatic merging',settings.autoMerge,'checkbox');
 openForm('Execution settings','Changes affect future scheduling. Push and merge remain sensitive actions.',
  html,async f=>{
   const x=values(f);
   const body={maxParallel:Number(x.maxParallel),maxAgentSessions:Number(x.maxAgentSessions),
    maxReviewRounds:Number(x.maxReviewRounds),leaseSeconds:Number(x.leaseSeconds),
    pollSeconds:Number(x.pollSeconds),allowPush:f.elements.allowPush.checked,
    autoMerge:f.elements.autoMerge.checked};
   for(const key of ['goalReservationTokens','goalExtensionIncrementTokens',
    'goalExtensionThresholdTokens','maxJobReservationTokens'])
    if(Object.hasOwn(x,key)){
     const value=Number(x[key]);
     if(!Number.isSafeInteger(value)||value<0)throw Error('Budget allocations require nonnegative integers');
     body[key]=value;
    }
   if(body.maxJobReservationTokens!==undefined && body.goalReservationTokens!==undefined &&
      body.maxJobReservationTokens<body.goalReservationTokens)
    throw Error('Per-job allocation limit must be at least the initial reservation');
   if((body.allowPush&&!settings.allowPush)||(body.autoMerge&&!settings.autoMerge)){
    if(!confirm('Enable publishing or automatic merging? Ensure repository policies and approvals are configured.'))return;
   }
   await mutate('settings','PUT',body);
   await fetchSettings();
  },'Apply settings');
}
async function confirmControl(type,id,action){const label={activate:'Activate',pause:'Pause',disable:'Disable',archive:'Archive',stop:'Stop',resume:'Resume',retry:'Retry',complete:'Complete',drain:'Drain',enable:'Enable'}[action]||action;
 let html=`<p class="subtext">${esc(label)} ${esc(type)}? The action will be recorded in the audit trail.</p>`+field('reason','Reason (optional)','','textarea');if(type==='worker'&&action==='stop')html+='<p class="subtext">Stops only a locally managed, idle worker process. Drain first if work is active.</p>';if(type==='job'&&['resume','retry'].includes(action)){const j=state.jobs.find(j=>j.id===id);if(j?.startedAt)html+=field('reviewed','I inspected the existing worktree and side effects',false,'checkbox')}
 openForm(`${label} ${type}`,action==='pause'&&type==='queue'?'Active jobs continue; new scheduling pauses.':'Confirm this operational action.',html,async f=>{const x=values(f);const body={action,reason:x.reason||'',reviewed:!!f.elements.reviewed?.checked};if(type==='job'&&['resume','retry'].includes(action)&&state.jobs.find(j=>j.id===id)?.startedAt&&!body.reviewed)throw Error('Worktree review acknowledgement required');await mutate(`${type}s/${encodeURIComponent(id)}/action`,'POST',body);if(type==='worker')await fetchManagedWorkers()},label)}
async function runQueue(action){if(action==='stop'){if(!confirm('Stop local active jobs at their next cancellation point and pause the queue? Remote work is not force-killed.'))return}await mutate('queue/'+action,'POST',{})}
async function verify(id){if(!confirm('Verify this job is integrated into its base branch?'))return;await mutate(`jobs/${id}/verify`,'POST',{})}
const plansCache=new Map(), planGoals=new Map();
let managedWorkers=[], planDraftGoal=null;
async function loadPlans(){
 if(!token)return;
 try{
  const plans=await api('plans');
  plansCache.clear();planGoals.clear();
  for(const entry of plans){
   planGoals.set(entry.id,entry);
   if(entry.plan)plansCache.set(entry.id,entry.plan);
  }
  if(view==='plans')render();
 }catch{}
}
function planGoalContext(goal){
 if(!goal)return '';
 const criteria=goal.acceptanceCriteria.map(item=>'<li>'+esc(item)+'</li>').join('');
 return '<h3>Current goal</h3><p class="subtext">'+esc(goal.objective)+'</p>'+
  (criteria?'<ul aria-label="Acceptance criteria">'+criteria+'</ul>':'');
}
async function planEditor(goalId){
 await loadPlans();
 const goal=planGoals.get(goalId);
 if(!goal||goal.state!=='draft')throw Error('Reload the goal planner before editing this draft');
 const existing=plansCache.get(goalId);
 planGoal=goalId;
 planDraftGoal=goal;
 planDraft={...structuredClone(existing||{goalId,projectId:goal.projectId,packages:[]}),goalRevision:goal.goalRevision};
 renderPlanEditor();
}
function renderPlanEditor(){if(!planDraft)return;const html=`<div class="card" style="margin-top:17px"><div class="card-head"><div><h2>Work packages</h2><p>Select dependencies by title. The server validates paths and cycles before saving.</p></div><button class="button button-muted" data-act="package-add">${ico('plus')} Add package</button></div><div class="card-body"><div class="plan-packages">${planDraft.packages.length?planDraft.packages.map((p,i)=>`<div class="package-row"><span class="package-num">${i}</span><div><strong>${esc(p.title)}</strong><small>${esc(shorten(p.description,100))} · Priority ${p.priority} · ${esc(p.risk)} · Depends on ${esc(p.dependsOn.map(n=>planDraft.packages[n]?.title||('Package '+n)).join(', ')||'none')}</small></div><div class="tiny-actions"><button class="tiny-button" data-act="package-edit" data-index="${i}">Edit</button><button class="tiny-button" data-act="package-remove" data-index="${i}">Remove</button></div></div>`).join(''):empty('route','No packages yet','Add work packages and dependencies before saving.')}</div><div class="head-actions" style="margin-top:20px;justify-content:flex-end"><button class="button button-muted" data-act="plan-close">Cancel</button><button class="button button-primary" data-act="plan-save">${ico('check')} Save plan</button></div></div></div>`;$('inspector-title').textContent='Plan Designer';$('inspector-body').innerHTML=`${planGoalContext(planDraftGoal)}${html}`;showInspector()}
function packageForm(i){
 const packages=planDraft.packages;
 const p=packages[i]||{title:'',description:'',risk:'medium',priority:50,files:[],dependsOn:[]};
 const options=packages.map((candidate,n)=>n===i?'':`<label class="checkbox-line">
  <input type="checkbox" name="dependency" value="${n}" ${p.dependsOn.includes(n)?'checked':''}> ${esc(candidate.title)}</label>`).join('');
 const html=field('title','Package title',p.title)+field('description','Description / acceptance criteria',p.description,'textarea')+
  `<div class="field-row">${field('risk','Risk',p.risk,'select',[['low','Low'],['medium','Medium'],['high','High']])}${field('priority','Priority',p.priority,'number')}</div>`+
  field('files','Expected file paths, one per line',p.files.join('\n'),'textarea')+
  (options?`<fieldset><legend>Depends on</legend>${options}</fieldset>`:'');
 openForm(i===packages.length?'Add work package':'Edit work package','A dependency must integrate before this package can start.',html,async f=>{
  const x=values(f);
  const item={title:x.title.trim(),description:x.description.trim(),risk:x.risk,priority:Number(x.priority),
   files:lines(x.files),dependsOn:new FormData(f).getAll('dependency').map(Number)};
  if(!item.title||!item.description||!item.files.length)throw Error('Title, description and expected paths are required');
  if(!Number.isInteger(item.priority)||item.priority<0||item.priority>100)throw Error('Priority must be an integer from 0 to 100');
  packages[i]=item;
  renderPlanEditor();
 });
}

let inspectorKey=null;
function jobInspectorDetails(j,kv){
 const owner=j.leaseOwner||'Unassigned';
 const workerId=owner.startsWith('remote:')?owner.slice(7):owner;
 const worker=state.workers.find(w=>w.id===workerId);
 const heartbeat=worker?(workerActive(worker)?'Connected, ':'Stale or offline, ')+relative(worker.heartbeat):'No registered heartbeat';
 const dependencies=(j.dependsOn||[]).map(id=>{const p=state.jobs.find(x=>x.id===id);return p?`${shorten(p.title,55)}: ${p.integrated?'integrated':p.state}`:'Unresolved '+id}).join('; ')||'None';
 const reservations=(state.budgetReservations||[]).filter(r=>r.jobId===j.id);
 const current=reservations.find(r=>r.executionGeneration===j.executionGeneration&&r.state==='active');
 const held=reservations.filter(r=>r.state==='held');
 const risk=recoveryExplanation(j);
 const active=JobStatesActiveUi.includes(j.state);
 const leaseLive=active&&j.leaseUntil&&new Date(j.leaseUntil).getTime()>Date.now();
 const next=risk.git?'Reconcile external Git effects before retry':risk.lease?'Current execution may still be active':held.length?'Reconcile held usage before a new claim':j.state==='queued'?'Wait for scheduling and integrated dependencies':active?'Monitor current execution':'Inspect the checkpoint and process before requeue';
 return `<hr class="panel-divider"><h3>Execution identity and health</h3><div class="job-info-grid">
 ${kv('Worker / owner',owner)}${kv('Worker heartbeat',heartbeat)}
 ${kv('Execution generation',j.executionGeneration??'Unavailable')}
 ${kv('Lease',j.leaseUntil?(leaseLive?'Live until ':'Inactive or expired at ')+dateTime(j.leaseUntil):'No active lease')}
 ${kv('Started',dateTime(j.startedAt))}${kv('Updated',dateTime(j.updatedAt))}${kv('Finished',dateTime(j.finishedAt))}
 </div><h3>Work package scope</h3><p class="subtext">${esc((j.expectedPaths||[]).join(', ')||'No declared paths')}</p>
 <div class="job-info-grid">${kv('Dependencies',dependencies)}${kv('Requested state',j.requestedState||'None')}</div>
 <h3>Budget and quality evidence</h3><div class="job-info-grid">
 ${kv('Observed token use',(Number(j.tokensIn||0)+Number(j.tokensOut||0)).toLocaleString())}
 ${kv('Active reservation',current?Number(current.reserved).toLocaleString()+' tokens':'None')}
 ${kv('Held reservations',held.length)}${kv('Final provider usage',j.usageComplete?'Reported':'Not confirmed')}
 ${kv('Evidence artifact',j.evidenceFile?'Path recorded':'Unavailable')}
 </div>
 <h3>Build checks and independent reviews</h3>
 <div id="inspector-job-quality" aria-live="polite"><p class="subtext">Loading commit-bound quality evidence...</p></div>
 <h3>Recovery assessment</h3><div class="recovery-assessment">
 <strong>Next safe step</strong><p>${esc(next)}</p>
 <p>Last durable checkpoint: ${esc(j.checkpointStage||'Unavailable')} · ${esc(j.checkpointCommit||'No commit recorded')} · ${esc(dateTime(j.checkpointAt))}</p>
 <p>Git side effects: ${risk.git?'Possibly occurred; reconcile externally':'No uncertain Git stage reported; absence is not proof'}</p>
 <p>Unsettled usage: ${risk.hold?'Held; controller reconciliation required':'No held reservation reported'}</p>
 </div>`;
}


async function renderJobProviderTurns(jobId){
 const target=$('inspector-provider-turns');
 if(!target)return;
 try{
  const turns=await api('jobs/'+jobId+'/provider-turns');
  if(inspectorKey!=='job:'+jobId||$('inspector-provider-turns')!==target)return;
  const usd=new Intl.NumberFormat(undefined,{style:'currency',currency:'USD',maximumFractionDigits:6});
  const rows=turns.map(t=>`<tr><td>${esc(t.providerId)}<small>${esc(t.role)} · Lease generation ${esc(t.executionGeneration)}</small></td>
   <td>${esc(t.requestedModel||'Unknown')}</td><td>${esc(t.observedModel||'Unavailable')}</td>
   <td>${number(t.reserved)} / ${number(t.consumed)}</td>
   <td>${Number.isFinite(t.reportedCostMicros)?usd.format(t.reportedCostMicros/1_000_000):'Unavailable'}</td>
   <td>${badge(t.state)}${t.holdReason?`<small>${esc(t.holdReason)}</small>`:''}</td>
   <td>${t.canRecoverReceipt?`<button class="tiny-button" data-act="recover-provider-receipt" data-id="${esc(jobId)}" data-index="${esc(t.id)}">Recover receipt</button>`:''}</td></tr>`).join('');
  target.innerHTML=rows?table(['Provider and route','Requested model','Observed model','Reserved / recorded','Reported USD','Status','Actions'],rows):
   '<p class="subtext">No remote provider turns recorded.</p>';
 }catch(error){
  if(inspectorKey==='job:'+jobId&&$('inspector-provider-turns')===target)
   target.textContent='Provider evidence unavailable: '+error.message;
 }
}

function recoverProviderReceipt(jobId,turnId){
 openForm('Recover executor receipt','The controller will validate the saved receipt and reconcile the recorded usage. The job stays blocked for separate review.',
  field('reason','Recovery evidence','','textarea')+
  field('reviewed','I independently reviewed the interrupted job',false,'checkbox')+
  field('confirmOverrun','I verified usage above the reserved tokens, if applicable',false,'checkbox'),
  async form=>{
   const reason=values(form).reason.trim();
   if(!form.elements.reviewed.checked||reason.length<10||reason.length>1000)
    throw Error('Confirm your independent review and enter 10–1000 characters of evidence');
   await mutate(`jobs/${jobId}/provider-turns/${turnId}/recover`,'POST',{
    reviewed:true,reason,confirmOverrun:form.elements.confirmOverrun.checked
   });
   await Promise.all([renderJobProviderTurns(jobId),fetchProviders()]);
  },'Reconcile receipt');
}

async function renderJobQuality(jobId){
 const target=$('inspector-job-quality');
 if(!target||inspectorKey!=='job:'+jobId)return;
 try{
  const evidence=await api('jobs/'+jobId+'/quality');
  if(inspectorKey!=='job:'+jobId||!$('inspector-job-quality'))return;
  const checks=(evidence.checks||[]).map(c=>`<tr><td>${esc(c.name)}</td>
   <td>${esc(c.exitCode)}</td><td>${badge(c.passed?'passed':'failed')}</td></tr>`).join('');
  const reviews=(evidence.reviews||[]).map(r=>`<tr><td>${esc(r.role)}</td>
   <td>${badge(r.verdict==='PASS'?'passed':'changes required')}</td></tr>`).join('');
  const findings=(evidence.findings||[]).map(f=>`<li>${esc(f)}</li>`).join('');
  const current=evidence.current===true;
  const overall=current?evidence.status:'stale';
  $('inspector-job-quality').innerHTML=`
   <p class="subtext">Evidence commit: ${esc(evidence.commit||'Unknown')}
   · ${current?'Matches current recorded job head':'Not verified for current job head'}</p>
   <p>${badge(overall)} ${evidence.verifiedAt?' · Captured '+esc(dateTime(evidence.verifiedAt)):''}</p>
   ${checks?table(['Check','Exit code','Result'],checks):'<p class="subtext">No check reports available.</p>'}
   ${reviews?table(['Review role','Verdict'],reviews):'<p class="subtext">No review verdicts available.</p>'}
   ${findings?`<h4>Findings</h4><ul>${findings}</ul>`:''}
   <p class="subtext">This view summarizes persisted evidence only. A textual reviewer verdict is not an independent security certification.</p>`;
 }catch(error){
  if(inspectorKey==='job:'+jobId&&$('inspector-job-quality'))
   $('inspector-job-quality').innerHTML=`<p class="subtext" role="status">Quality evidence unavailable: ${esc(error.message)}</p>
    <button class="button button-muted" data-act="quality-refresh" data-id="${jobId}">Retry</button>`;
 }
}

async function renderGoalBudget(goalId){
 const target=$('inspector-goal-budget');
 if(!target||inspectorKey!=='goal:'+goalId)return;
 try{
  const budget=await api('goals/'+goalId+'/budget');
  if(inspectorKey!=='goal:'+goalId||!$('inspector-goal-budget'))return;
  const nf=x=>Number(x||0).toLocaleString();
  const rows=budget.reservations||[];
  const active=budget.activeOutstanding;
  const held=budget.heldOutstanding;
 const goal=state.goals.find(g=>g.id===goalId);
 const providerRows=(budget.providers||[]).map(p=>`<tr><td>${esc(p.providerId)}</td><td>${esc(p.model||"Provider default")}</td><td>${esc(p.role)}</td><td>${nf(p.inputTokens)}</td><td>${nf(p.outputTokens)}</td></tr>`).join("");
  const total=Number(budget.total||0),used=Number(budget.consumed||0),committed=Number(budget.committed||0);
  const percent=total>0?Math.min(100,Math.max(0,100*(used+committed)/total)):0;
  const records=rows.map(r=>{const j=state.jobs.find(j=>j.id===r.jobId);
   return `<tr><td>${esc(j?shorten(j.title,38):r.jobId.slice(0,8))}<small>Generation ${esc(r.executionGeneration)}</small></td><td>${badge(r.state)}</td><td>${nf(r.reserved)}</td><td>${nf(r.consumed)}</td><td>${nf(r.outstanding)}</td></tr>`;
  }).join('');
  const jobs=new Set(state.jobs.filter(j=>j.goalId===goalId).map(j=>j.id));
  const events=state.events.filter(e=>jobs.has(e.jobId)&&['budget_reserved','budget_extended','budget_held','budget_released','budget_reconciled','budget_overrun'].includes(e.type));
  const durableExtensions=Array.isArray(budget.extensions)?budget.extensions:null;
  const grantRows=(durableExtensions||[]).map(e=>{
   const job=state.jobs.find(j=>j.id===e.jobId);
   const owner=e.workerId||'Controller';
   return `<tr><td><strong>${esc(shorten(job?.title||e.jobId||'Unknown',40))}</strong>
    <small>Generation ${esc(e.executionGeneration)}</small></td>
    <td>${esc(dateTime(e.at))}</td><td>${esc(owner)}</td>
    <td>${nf(e.granted)}</td><td>${nf(e.authorizedTotal)}</td></tr>`;
  }).join('');
  // Extended job accounting is optional until the budget API change is integrated.
  const accountedJobs=Array.isArray(budget.jobs)?budget.jobs:state.jobs.filter(j=>j.goalId===goalId).map(j=>({
   id:j.id,state:j.state,stage:j.stage,worker:j.leaseOwner||null,
   tokensIn:j.tokensIn,tokensOut:j.tokensOut,usageComplete:j.usageComplete
  }));
  const usageRows=accountedJobs.map(entry=>{
   const job=state.jobs.find(j=>j.id===entry.id);
   const total=Number(entry.tokensIn||0)+Number(entry.tokensOut||0);
   return `<tr><td><strong>${esc(shorten(job?.title||entry.id,45))}</strong><small>${esc((entry.state||'unknown')+' / '+(entry.stage||'unknown'))}</small></td>
    <td>${esc(entry.worker||'No active owner')}</td><td>${nf(entry.tokensIn)}</td><td>${nf(entry.tokensOut)}</td>
    <td>${nf(total)}</td><td>${entry.usageComplete?badge('reported'):badge('unconfirmed')}</td></tr>`;
  }).join('');
  $('inspector-goal-budget').innerHTML=`<div class="budget-breakdown">
    <div><small>Total budget</small><strong>${nf(total)}</strong></div><div><small>Observed consumption</small><strong>${nf(used)}</strong></div>
    <div><small>Active liabilities</small><strong>${nf(active)}</strong></div><div><small>Held liabilities</small><strong>${nf(held)}</strong></div>
    <div><small>Unreserved capacity</small><strong>${nf(budget.available)}</strong></div>
   </div><div class="progress-track" role="meter" aria-label="Consumed and reserved goal capacity" aria-valuenow="${percent.toFixed(1)}" aria-valuemin="0" aria-valuemax="100"><div class="progress-fill" style="width:${percent.toFixed(1)}%"></div></div>
   <p class="subtext">Goal-budget accounting is separate from provider account allowances. Final usage that is unknown must remain unsettled.</p>
   <h3>Reservations</h3>${rows.length?table(['Job','Status','Reserved','Consumed','Outstanding'],records):'<p class="subtext">No reservations reported.</p>'}
   ${rows.some(r=>r.pendingProviderTurns)?`<p><button class="button button-muted" data-act="linked-provider-usage">Review linked provider turns in Capacity</button></p>`:''}
    ${rows.some(r=>r.state==='held'&&!r.pendingProviderTurns)?`<button class="button button-muted" data-act="budget-reconcile" data-id="${goalId}">Review held usage</button>`:''}
   ${budget.planningHeld>0?'<p class="subtext">Reconcile held planning tokens in Capacity → Held provider reservations.</p>':''}
   <h3>Goal usage by provider</h3>
   ${providerRows?table(['Provider','Model','Role','Input','Output'],providerRows):'<p class="subtext">No attributed provider usage for this goal yet.</p>'}
   ${goal?`<div id="inspector-provider-capacity" data-project="${esc(goal.projectId)}">${projectProviderCapacity(goal.projectId)}</div>`:''}
   <h3>Per-job reported usage</h3>
   ${usageRows?table(['Job','Current owner','Input','Output','Total','Final usage'],usageRows):'<p class="subtext">No linked job usage reported.</p>'}
   <p class="subtext">Owner is the current lease assignment, not a complete historical worker attribution. Unconfirmed final usage is not a provider-account allowance.</p>
   <h3>Controller-authorized extension history</h3>
   ${durableExtensions===null?'<p class="subtext">Dedicated extension history is unavailable on this controller version.</p>':
    grantRows?table(['Job','Granted at','Controller identity','Additional tokens','New reservation'],grantRows):
    '<p class="subtext">No extension requests have been recorded in the durable ledger.</p>'}
   <h3>Budget activity</h3>${eventItems(events,20)}
   <p class="subtext">Operational events are subject to controller retention limits. The durable extension ledger, when supported, begins at its migration date; older events cannot be reconstructed automatically.</p>`;
 }catch(error){
  if(inspectorKey==='goal:'+goalId&&$('inspector-goal-budget'))
   $('inspector-goal-budget').innerHTML=`<p class="subtext" role="alert">Budget details unavailable: ${esc(error.message)}</p><button class="button button-muted" data-act="budget-refresh" data-id="${goalId}">Retry</button>`;
 }
}
async function reconcileGoalBudget(goalId){
 const budget=await api('goals/'+goalId+'/budget');
 const held=(budget.reservations||[]).filter(r=>r.state==='held'&&!r.pendingProviderTurns);
 if(!held.length){notify('Review linked provider turns in Capacity before settling the goal');return}
 const options=held.map(r=>[r.id,(state.jobs.find(j=>j.id===r.jobId)?.title||r.jobId)+' · '+r.outstanding+' outstanding']);
 const first=held[0],job=(budget.jobs||[]).find(j=>j.id===first.jobId);
 openForm('Reconcile held usage','Administrator action. Only independently verified final provider usage can release unknown liabilities.',
  field('reservationId','Held reservation',first.id,'select',options)+
  field('finalTokensIn','Final input tokens',job?.tokensIn||0,'number')+
  field('finalTokensOut','Final output tokens',job?.tokensOut||0,'number')+
  field('reason','Evidence and review justification','','textarea')+
  field('reviewed','I independently verified the final usage',false,'checkbox')+
   field('confirmOverrun','I verified usage above the reserved tokens, if applicable',false,'checkbox'),
  async form=>{
   const x=values(form),input=Number(x.finalTokensIn),output=Number(x.finalTokensOut);
   if(!form.elements.reviewed.checked||x.reason.trim().length<10||
      !Number.isSafeInteger(input)||!Number.isSafeInteger(output)||input<0||output<0)
    throw Error('Verified nonnegative integer totals and a justification of at least 10 characters are required');
   if(!confirm('Release unused reserved capacity based on independently verified final usage? This action is recorded.'))return;
   await mutate(`goals/${goalId}/budget/reconcile`,'POST',{
     reservationId:x.reservationId,finalTokensIn:input,finalTokensOut:output,reviewed:true,reason:x.reason.trim(),
      confirmOverrun:form.elements.confirmOverrun.checked
   });
   await renderGoalBudget(goalId);
  },'Confirm reconciliation');
 const form=$('modal-form');
 form.elements.reservationId.addEventListener('change',()=>{
  const selected=held.find(r=>r.id===form.elements.reservationId.value);
  const totals=(budget.jobs||[]).find(j=>j.id===selected?.jobId);
  form.elements.finalTokensIn.value=totals?.tokensIn??0;
  form.elements.finalTokensOut.value=totals?.tokensOut??0;
  form.elements.reviewed.checked=false;
  form.elements.confirmOverrun.checked=false;
 });
}

function showInspector(){inspector=true;$('inspector').classList.add('open');$('inspector').setAttribute('aria-hidden','false');$('inspector-backdrop').classList.add('open')}
function closeInspector(){inspectorKey=null;inspector=null;$('inspector').classList.remove('open');$('inspector').setAttribute('aria-hidden','true');$('inspector-backdrop').classList.remove('open')}
function inspectorPanel(title,body){inspectorKey=null;$('inspector-title').textContent=title;$('inspector-body').innerHTML=body;showInspector()}
function inspect(type,id){const entity=(type==='project'?state.projects:type==='goal'?state.goals:type==='worker'?state.workers:state.jobs).find(x=>(type==='worker'?x.id: x.id)===id);if(!entity)return;let body='';const kv=(label,v)=>`<div class="kv"><small>${esc(label)}</small><strong>${esc(v)}</strong></div>`;
 if(type==='job'){const j=entity;body=`${badge(j.state)}<div class="job-info-grid" style="margin-top:18px">${kv('Priority',j.priority)}${kv('Risk',j.risk)}${kv('Execution mode',j.mode)}${kv('Stage',j.stage)}${kv('Attempt',j.attempt)}${kv('Tokens',Number(j.tokensIn||0)+Number(j.tokensOut||0))}</div><h3>Description</h3><p class="subtext">${esc(j.description)}</p>${j.error?`<div class="badge danger">${esc(j.error)}</div>`:''}${kv('Branch',j.branch||'—')}${kv('Worktree',j.worktree||'—')}${kv('Head commit',j.headSha||'—')}${kv('Dependencies',j.dependsOn.join(', ')||'None')}${safeUrl(j.prUrl)?`<p><a href="${esc(safeUrl(j.prUrl))}" rel="noopener noreferrer" target="_blank">View pull request ↗</a></p>`:''}<hr class="panel-divider"><h3>Controls</h3><div class="queue-controls">${['queued'].includes(j.state)?`<button class="button button-primary" data-act="job-start" data-id="${j.id}" ${j.mode==='remote'?'disabled':''}>Run now</button><button class="button button-muted" data-control="job:${j.id}:pause">Pause</button><button class="button button-danger" data-control="job:${j.id}:stop">Stop</button>`:''}${['preparing','building','gating','reviewing','reworking','awaiting_approval'].includes(j.state)?`<button class="button button-muted" data-control="job:${j.id}:pause">Pause local</button><button class="button button-danger" data-control="job:${j.id}:stop">Stop local</button>`:''}${['paused','stopped','blocked','failed'].includes(j.state)?`<button class="button button-primary" data-control="job:${j.id}:resume">Review & requeue</button>`:''}${['pr_ready','completed_local'].includes(j.state)?`<button class="button button-muted" data-act="verify" data-id="${j.id}">Verify integration</button>`:''}</div>`;
 body+=jobInspectorDetails(j,kv);
 if(j.mode==='remote')body+='<hr class="panel-divider"><h3>Remote provider turns</h3><div id="inspector-provider-turns" aria-live="polite">Loading provider evidence...</div>';
  }else if(type==='project'){const p=entity;body=`${badge(p.status||'active')}<div class="job-info-grid" style="margin-top:18px">${kv('Base branch',p.baseBranch)}${kv('Concurrency',p.maxParallel)}</div>${kv('Path',p.path)}${kv('Origin',p.origin||'Local')}${kv('Checks',p.checks.length)}${kv('Protected paths',p.protectedPaths.join(', '))}<hr class="panel-divider"><div class="queue-controls"><button class="button button-muted" data-edit="project:${p.id}">Configure</button>${['paused','disabled'].includes(p.status)?`<button class="button button-primary" data-control="project:${p.id}:activate">Activate</button>`:`<button class="button button-muted" data-control="project:${p.id}:pause">Pause</button><button class="button button-muted" data-control="project:${p.id}:disable">Disable</button>`}<button class="button button-danger" data-control="project:${p.id}:archive">Archive</button></div>`;
 body+=`<p><button class="button button-muted" data-act="open-project-analysis" data-id="${p.id}">Review repository & plan goals</button></p><hr class="panel-divider"><h3>AI provider routing</h3><p class="subtext">Select models for planning, implementation and independent reviews. Configure local token ceilings per provider.</p><button class="button button-muted" data-act="project-providers" data-id="${p.id}">Configure AI providers</button>`;
 }else if(type==='goal'){const g=entity;body=`${badge(g.state)}${kv('Objective',g.objective)}${kv('Token budget',g.tokenBudget.toLocaleString())}<h3>Acceptance criteria</h3><ul>${g.acceptanceCriteria.map(c=>`<li>${esc(c)}</li>`).join('')}</ul><hr class="panel-divider"><div class="queue-controls"><button class="button button-muted" data-edit="goal:${g.id}">Edit</button>${g.state==='draft'?`<button class="button button-primary" data-control="goal:${g.id}:activate">Activate</button>`:''}${['active'].includes(g.state)?`<button class="button button-muted" data-control="goal:${g.id}:pause">Pause</button><button class="button button-muted" data-control="goal:${g.id}:stop">Stop</button><button class="button button-muted" data-control="goal:${g.id}:complete">Complete</button>`:''}${['paused','stopped'].includes(g.state)?`<button class="button button-primary" data-control="goal:${g.id}:activate">Resume</button>`:''}<button class="button button-muted" data-control="goal:${g.id}:archive">Archive</button></div>`;
 body+='<hr class="panel-divider"><h3>Goal budget ledger</h3><div id="inspector-goal-budget" aria-live="polite"><p class="subtext">Loading controller budget data...</p></div>';
  }else{const w=entity;body=`${badge(!w.enabled?'disabled':w.draining?'draining':workerActive(w)?'online':'offline')}${kv('Platform',w.platform)}${kv('Last heartbeat',dateTime(w.heartbeat))}<p class="subtext">Draining prevents new claims; it does not terminate in-flight work.</p><div class="queue-controls"><button class="button button-muted" data-act="worker-edit" data-id="${encodeURIComponent(w.id)}">Configure</button><button class="button button-muted" data-control="worker:${encodeURIComponent(w.id)}:${managedWorkers.includes(w.id)?'stop':'start'}">${managedWorkers.includes(w.id)?'Stop local':'Start local'}</button><button class="button button-muted" data-control="worker:${encodeURIComponent(w.id)}:enable">Enable</button><button class="button button-muted" data-control="worker:${encodeURIComponent(w.id)}:drain">Drain</button><button class="button button-danger" data-control="worker:${encodeURIComponent(w.id)}:disable">Disable</button></div>`;}
 const events=state.events.filter(e=>e.jobId===id).slice(-15);if(events.length)body+=`<hr class="panel-divider"><h3>Job activity</h3>${eventItems(events,15)}`;
 inspectorPanel((type==='job'?entity.title:type==='goal'?shorten(entity.objective,40):type==='project'?entity.name:entity.id),body);
  inspectorKey=type+':'+id;
  if(type==='goal')renderGoalBudget(id);
  if(type==='job'){renderJobQuality(id);renderJobProviderTurns(id);}
}
function preflightDetails(preflight){
 if(!preflight)return '<p class="subtext">Save or regenerate the plan to capture master and open work.</p>';
 const source=preflight.existingCode.map(c=>`<li><strong>${esc(c.path)}</strong><small>${esc(c.sample)}</small></li>`).join('');
 const jobs=preflight.existingJobs.map(j=>`<li>${esc(j.title)} · ${esc(j.state)}${j.integrated?' · verified integrated':''}</li>`).join('');
 const links=items=>(items||[]).map(item=>{
  const label=`#${esc(item.number)} ${esc(item.title)}`;
  const url=safeUrl(item.url);
  return `<li>${url?`<a href="${esc(url)}" rel="noopener noreferrer" target="_blank">${label}</a>`:label}${item.headCommit?` <code>${esc(item.headCommit.slice(0,12))}</code>`: ''}</li>`;
 }).join('');
 const section=(title,items,note)=>`<h4>${title}</h4>${note?`<p class="subtext">${esc(note)}</p>`:''}${items?`<ul>${items}</ul>`:'<p class="subtext">No matching candidates returned.</p>'}`;
 const remoteStatus={
  matched:'Matches local master', mismatch:'Differs from local master. Update the checkout before continuing.',
  missing:'Configured remote has no master branch.', unavailable:'Remote master could not be verified.',
  'local-only':'No remote configured.'
 }[preflight.remoteMasterStatus]||'Remote master was not checked.';
 return `<h3>Master-first preflight</h3>
 <p class="subtext">Local master · ${esc(preflight.baseCommit)} · ${esc(dateTime(preflight.capturedAt))}</p>
 <p class="subtext">Remote master · ${esc(preflight.remoteMasterCommit||'—')} · ${esc(remoteStatus)}</p>
 ${preflight.githubRepository?`<p class="subtext">GitHub inventory · ${esc(preflight.githubRepository)}</p>`:''}
 <p class="subtext">Inspect matching source, jobs, PRs and issues before adopting. Extend existing work where possible.</p>
 ${section('Existing source on master',source)}${section('Existing Factory work',jobs)}
 ${section('Open pull requests',links(preflight.openPullRequests),preflight.pullRequestsChecked?'Inventory checked':preflight.pullRequestCheckNote||'Inventory unchecked')}
 ${section('Open issues',links(preflight.openIssues),preflight.issuesChecked?'Inventory checked':preflight.issueCheckNote||'Inventory unchecked')}`;
}
async function act(action,id,index,revision){switch(action){case'open-connection':$('connection-modal').showModal();break;case'create-project':formProject();break;case'project-providers':formProjectProviders(id);break;
 case'analysis-run':await startProjectReview();break;case'analysis-goal':createReviewGoal(id);break;case'provider-reconcile':formProviderReconciliation(id);break;
 case'provider-recover':formProviderHostRecovery(id);break;
 case'linked-provider-usage':closeInspector();nav('usage');await fetchProviders();break;
 case'recover-provider-receipt':recoverProviderReceipt(id,index);break;
 case'project-plans':planProject=id;nav('plans');await loadPlans();break;
 case'open-project-analysis':closeInspector();analysisProject=id;nav('analysis');await loadProjectReview(id);break;case'create-goal':formGoal();break;case'create-job':formJob();break;case'create-worker':formWorker();break;case'worker-edit':formWorker(decodeURIComponent(id));break;case'edit-settings':formSettings();break;case'budget-refresh':await renderGoalBudget(id);break;case'budget-reconcile':await reconcileGoalBudget(id);break;case'quality-refresh':await renderJobQuality(id);break;
 case'queue-run':case'queue-pause':case'queue-resume':case'queue-stop':await runQueue(action.slice(6));break;
 case'verify':await verify(id);break;
 case'job-start':await mutate(`jobs/${id}/start`,'POST',{});break;
 case'plan-generate':{
 if(generatingPlans.has(id))break;
 if(plansCache.has(id)&&!confirm('Replace the saved plan with a new proposal?'))break;
 generatingPlans.add(id);render();
 try{
  const expected=plansCache.get(id)?.revision||'00000000-0000-0000-0000-000000000000';
  const plan=await api('plans/'+id+'/generate?wait=true&planRevision='+encodeURIComponent(expected),'POST',{});
  plansCache.set(id,plan);
  notify('Plan generated');
  if(view==='plans')await act('plan-view',id);
 }finally{generatingPlans.delete(id);render();await fetchProviders()}
 break;}
 case'plan-edit':await planEditor(id);break;
 case'plan-preflight':{
 const evidence=await api('goals/'+id+'/preflight');
 inspectorPanel('Check master & open work',preflightDetails(evidence));
 break;}
 case'plan-view':{
 const plan=plansCache.get(id);if(!plan)return;
 const goal=planGoals.get(id);
 const packages=plan.packages.map((p,i)=>'<div class="package-row"><span class="package-num">'+(i+1)+'</span><div><strong>'+
  esc(p.title)+'</strong><small>'+esc(p.description)+'</small><small>Depends on '+esc(p.dependsOn.map(n=>plan.packages[n]?.title).join(', ')||'none')+
  '</small><small>Paths '+esc(p.files.join(', '))+'</small></div></div>').join('');
 const adoptable=plan.revision&&plan.preflight&&plan.goalRevision===goal?.goalRevision;
 const adopt=goal?.state!=='draft'?'':adoptable
  ?'<button class="button button-primary" data-act="plan-adopt" data-id="'+id+'" data-revision="'+esc(plan.revision)+'">Adopt reviewed plan</button>'
  :'<span class="subtext">Review the current goal, then edit and save or regenerate this plan before adoption.</span>';
 inspectorPanel('Plan details',planGoalContext(goal)+preflightDetails(plan.preflight)+
  '<h3>Proposed work packages</h3><div class="plan-packages">'+packages+'</div><p>'+adopt+'</p>');
 break;}
 case'plan-adopt':{
 if(!revision)throw Error('Open the current plan revision before adopting');
 if(!confirm('Before adopting: have you reviewed existing master code, partial implementations and relevant open PRs and issues, and confirmed that no duplicate work package will be created?'))return;
 await mutate(`plans/${id}/adopt`,'POST',{mode:'local',reviewedExistingWork:true,planRevision:revision});
 closeInspector();
 break;
}
 case'plan-close':closeInspector();planDraft=null;break;
 case'package-add':packageForm(planDraft.packages.length);break;
 case'package-edit':packageForm(Number(index));break;
 case'package-remove':{const i=Number(index);if(planDraft.packages.some((p,n)=>n!==i&&p.dependsOn.includes(i))){notify('Remove dependent references first',true);return}planDraft.packages.splice(i,1);for(const p of planDraft.packages)p.dependsOn=p.dependsOn.map(n=>n>i?n-1:n);renderPlanEditor();break;}
 case'plan-save':{
 if(!planDraft?.packages.length){notify('Add at least one work package',true);return}
 const goalId=planGoal;
 const saved=await mutate('plans/'+goalId,'PUT',planDraft);
 plansCache.set(goalId,saved);
 render();
 await act('plan-view',goalId);
 break;}
 }}
function showPalette(){const commands=[['Overview','overview','overview'],['Projects','projects','folder'],['Goals','goals','target'],['Project review','analysis','eye'],['Goal planner','plans','route'],['Jobs','jobs','layers'],['Queue','queue','queue'],['Recovery','recovery','shield'],['Workers','workers','cpu'],['Activity','events','activity'],['Settings','settings','settings']];const q=$('palette-input').value.toLowerCase();$('palette-results').innerHTML=commands.filter(([a])=>a.toLowerCase().includes(q)).map(([title,to,i])=>`<button class="palette-entry" data-view="${to}">${ico(i)}${title}<small>Navigate</small></button>`).join('')||`<div class="empty">No matching commands</div>`}
document.addEventListener('click',async e=>{const btn=e.target.closest('[data-view],[data-act],[data-inspect],[data-edit],[data-control]');if(!btn)return;try{
 if(projectAccess()){
  const action=btn.dataset.act;
  if(btn.dataset.view==='recovery'){nav('recovery');await loadProjectRecovery();}
  if(action==='refresh-project-recovery')await loadProjectRecovery();
  if(action==='settle-project-goal'||action==='settle-project-provider'||action==='recover-project-receipt')
   formProjectRecovery(action,btn.dataset.id);
  return;
 }
 if(btn.dataset.view){nav(btn.dataset.view);if($('palette').open)$('palette').close();if(view==='plans')loadPlans();if(view==='analysis')loadProjectReview();return}
 if(btn.dataset.inspect){const [type,id]=btn.dataset.inspect.split(':');inspect(type,decodeURIComponent(id));return}
 if(btn.dataset.edit){const [type,id]=btn.dataset.edit.split(':');if(type==='project')formProject(id);else if(type==='goal')formGoal(id);else if(type==='job')formJob(id);return}
 if(btn.dataset.control){const bits=btn.dataset.control.split(':');await confirmControl(bits[0],decodeURIComponent(bits[1]),bits[2]);return}
 if(btn.dataset.act)await act(btn.dataset.act,btn.dataset.id,btn.dataset.index,btn.dataset.revision)
 }catch(error){notify(error.message,true)}});
$('modal-form').addEventListener('submit',async e=>{e.preventDefault();const btn=$('modal-submit'),handler=modalHandler;btn.disabled=true;try{if(handler)await handler(e.target);if(handler===modalHandler)$('form-modal').close()}catch(error){if(handler===modalHandler)notify(error.message,true)}finally{btn.disabled=false}});
$('modal-close').addEventListener('click',()=>{$('form-modal').close()});$('modal-cancel').addEventListener('click',()=>$('form-modal').close());
$('connection-form').addEventListener('submit',async e=>{e.preventDefault();await connect($('api-token').value.trim())});
$('disconnect').addEventListener('click',()=>{$('connection-modal').close();connect('')});
document.querySelectorAll('[data-close]').forEach(el=>el.addEventListener('click',()=>$(el.dataset.close).close()));
$('brand-connection').addEventListener('click',()=>{$('api-token').value=token;$('connection-modal').showModal()});
$('inspector-close').addEventListener('click',closeInspector);$('inspector-backdrop').addEventListener('click',closeInspector);
$('mobile-open').addEventListener('click',()=>{$('sidebar').classList.add('open');$('sidebar-backdrop').classList.add('open')});
$('mobile-close').addEventListener('click',()=>{$('sidebar').classList.remove('open');$('sidebar-backdrop').classList.remove('open')});
$('sidebar-backdrop').addEventListener('click',()=>{$('sidebar').classList.remove('open');$('sidebar-backdrop').classList.remove('open')});
$('top-create').addEventListener('click',()=>{const actions=[['Project','create-project'],['Goal','create-goal'],['Job','create-job'],['Worker','create-worker']];openForm('Create new','Choose the object to add to your Factory workspace.',`<div class="list-stack">${actions.map(([name,action])=>`<button type="button" class="palette-entry" data-act="${action}">${ico('plus')} New ${name}</button>`).join('')}</div>`,async()=>{},'Done')});
$('open-search').addEventListener('click',()=>{if(projectAccess())return;$('palette-input').value='';showPalette();$('palette').showModal();$('palette-input').focus()});
$('palette-input').addEventListener('input',showPalette);
document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){e.preventDefault();$('open-search').click()}if(e.key==='Escape'&&inspector)closeInspector()});
setInterval(statusFromHealth,1100);
setInterval(()=>{if(token&&Date.now()-lastStateUpdate>11000)refreshState()},10000);
setInterval(refreshUsage,60000);
setInterval(()=>{if(view==='plans'&&token)loadPlans();if(view==='workers'&&token)fetchManagedWorkers()},15000);
document.querySelectorAll('[data-icon]').forEach(el=>{el.innerHTML=ico(el.dataset.icon)});
render();statusFromHealth();if(token)connect();else setTimeout(()=>$('connection-modal').showModal(),350);
