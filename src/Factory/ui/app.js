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
let settings={}, usage=null, health=null, view='overview', search='', filter='all', streamController=null,lastHeartbeat=0, connected=false, lastStateUpdate=0, modalHandler=null, inspector=null, planDraft=null, planGoal=null, loading=false;
let providerCapacityCache={}, analysisReports={}, capacityProjectId='', analysisProjectId='', analysisBusy=false;
async function api(path,method='GET',body){if(!token)throw new Error('Connect your API token first');const res=await fetch('/api/'+path,{method,headers:{'Authorization':'Bearer '+token,...(body!==undefined?{'Content-Type':'application/json'}:{})},body:body===undefined?undefined:JSON.stringify(body),cache:'no-store'});if(!res.ok){let msg='Request failed ('+res.status+')';try{let obj=await res.json();msg=obj.error||obj.title||msg}catch{}throw new Error(msg)}if(res.status===204||res.status===202)return null;return res.json()}
const notify=(message,error=false)=>{const el=document.createElement('div');el.className='toast'+(error?' error':'');el.innerHTML=`<span class="toast-icon">${ico(error?'shield':'check')}</span><span>${esc(message)}</span>`;$('toast-stack').append(el);setTimeout(()=>el.remove(),5500)};
function condition(cls, val) { document.querySelectorAll(cls).forEach(el=>el.classList.toggle('hidden',!val)); }
function nav(v){view=v;filter='all';search='';document.querySelectorAll('.nav-item').forEach(b=>b.classList.toggle('active',b.dataset.view===v));$('breadcrumb').textContent=v.charAt(0).toUpperCase()+v.slice(1);$('sidebar').classList.remove('open');$('sidebar-backdrop').classList.remove('open');render();if(v==='usage')refreshProviderCapacity();if(v==='analysis')loadProjectAnalysis();$('main').focus({preventScroll:true});window.scrollTo({top:0,behavior:'instant'});}
function statusFromHealth(){const since=Date.now()-lastHeartbeat;const status=connected&&since<8500?'connected':connected&&since<17000?'degraded':'offline';
 const label=status==='connected'?'Connected':status==='degraded'?'Degraded':'Offline';$('connection-pill').className='connection-pill '+(status==='connected'?'':status);$('connection-label').textContent=label;$('connection-age').textContent=since<60000&&lastHeartbeat?Math.max(0,Math.round(since/1000))+'s':'';
 for(const id of ['connection-dot','sidebar-dot','footer-dot'])$(id).className='live-dot '+(status==='connected'?'':status);
 $('sidebar-status').textContent=status==='connected'?'System connected':status==='degraded'?'Connection delayed':'Connection offline';$('sidebar-heartbeat').textContent=lastHeartbeat?'Heartbeat '+relative(new Date(lastHeartbeat).toISOString()):'No heartbeat';
 $('footer-status').textContent=status==='connected'?'Realtime stream active':status==='degraded'?'Reconnecting stream':'No active connection';$('status-clock').textContent=new Date().toLocaleTimeString();}
function updateHealth(h){health=h;lastHeartbeat=Date.now();connected=true;statusFromHealth()}
async function startStream(){if(streamController)streamController.abort();streamController=new AbortController();const controller=streamController;
 while(!controller.signal.aborted&&token){try{const res=await fetch('/api/events/stream',{headers:{Authorization:'Bearer '+token},cache:'no-store',signal:controller.signal});if(!res.ok)throw Error('Stream '+res.status);
 const reader=res.body.getReader(),decoder=new TextDecoder();let buffer='';while(!controller.signal.aborted){const {done,value}=await reader.read();if(done)break;buffer+=decoder.decode(value,{stream:true});let pos;while((pos=buffer.indexOf('\n\n'))>=0){const message=buffer.slice(0,pos);buffer=buffer.slice(pos+2);const data=message.split('\n').find(l=>l.startsWith('data: '));if(data){try{updateHealth(JSON.parse(data.slice(6)));if(Date.now()-lastStateUpdate>2200)await refreshState()}catch(e){console.warn('Heartbeat message:',e)}}}}
 }catch(e){if(controller.signal.aborted)break;connected=false;statusFromHealth()}await new Promise(r=>setTimeout(r,2800))}
}
async function refreshState(){if(!token||loading)return;loading=true;try{state=await api('state');lastStateUpdate=Date.now();connected=true;render();statusFromHealth()}catch(e){connected=false;statusFromHealth();if(!lastStateUpdate)render();}finally{loading=false}}
async function refreshUsage(){if(!token)return;try{const u=await api('usage');usage=(!Number.isFinite(u?.weekly?.usedPercent)&&!Number.isFinite(u?.shortWindow?.usedPercent))?null:{...u,weekly:Number.isFinite(u?.weekly?.usedPercent)&&Number.isFinite(u?.weekly?.remainingPercent)?u.weekly:null,shortWindow:Number.isFinite(u?.shortWindow?.usedPercent)&&Number.isFinite(u?.shortWindow?.remainingPercent)?u.shortWindow:null}}catch{usage=null}condition('#usage-nav',!!usage||state.projects.length>0);if(view==='usage'&&!usage&&!state.projects.length)nav('overview');else render()}
async function connect(nextToken){if(nextToken!==undefined){token=nextToken;try{token?sessionStorage.setItem('factory-token',token):sessionStorage.removeItem('factory-token')}catch{}}
 if(!token){connected=false;if(streamController)streamController.abort();state={projects:[],goals:[],jobs:[],workers:[],events:[],graph:[],knowledge:[],evaluations:[],releases:[],paused:false};usage=null;providerCapacityCache={};analysisReports={};render();statusFromHealth();return}
 try{await refreshState();if(!connected)throw Error('Invalid token or server unreachable');$('connection-modal').close();startStream();refreshUsage();fetchSettings();fetchManagedWorkers()}catch(e){notify(e.message,true);$('connection-modal').showModal()}}
async function fetchSettings(){try{settings=await api('settings')}catch{}}
async function fetchManagedWorkers(){try{managedWorkers=await api('workers/managed')}catch{managedWorkers=[]}}
async function mutate(path, method='POST',data){try{const result=await api(path,method,data);notify('Changes applied');await refreshState();if(path.includes('/plans/'))render();return result}catch(e){notify(e.message,true);throw e}}
function header(title,desc,action,actionLabel){return `<div class="page-head"><div><div class="eyebrow">MISSION CONTROL / ${esc(view.toUpperCase())}</div><h1>${esc(title)}</h1><p>${esc(desc)}</p></div><div class="head-actions">${action?`<button class="button button-primary" data-act="${esc(action)}">${ico('plus')}${esc(actionLabel||'Create new')}</button>`:''}</div></div>`}
function metric(label,value,foot,iconName){return `<div class="metric-card"><div class="metric-top">${esc(label)}${ico(iconName)}</div><div class="metric-value">${esc(value)}</div><div class="metric-foot">${esc(foot)}</div></div>`}
function card(title,desc,body,link,actions=''){return `<section class="card"><div class="card-head"><div><h2>${esc(title)}</h2><p>${esc(desc)}</p></div><div class="card-actions">${actions}${link?`<button class="subtle-link" data-view="${link}">View all ${ico('arrow')}</button>`:''}</div></div><div class="card-body">${body}</div></section>`}
function empty(iconName,title,desc,action,label){return `<div class="empty"><div class="empty-icon">${ico(iconName)}</div><h3>${esc(title)}</h3><p>${esc(desc)}</p>${action?`<button class="button button-muted" data-act="${action}">${ico('plus')}${esc(label||'Create new')}</button>`:''}</div>`}
function eventItems(events,count=8){return events.length?events.slice(-count).reverse().map(e=>`<div class="event-line"><div class="event-marker"></div><div><strong>${esc(e.type.replaceAll('_',' '))}</strong><small>${esc(relative(e.at))}${e.jobId?' · '+esc(e.jobId.slice(0,8)):''}</small><p>${esc(shorten(e.message,150))}</p></div></div>`).join(''):empty('activity','No activity yet','Factory events will appear here as work is performed.');}
function workerActive(w){return Date.now()-new Date(w.heartbeat).getTime()<20000}
function usageCards(){if(!usage)return '';const format=(w,label)=>{if(!w||!Number.isFinite(w.usedPercent)||!Number.isFinite(w.remainingPercent))return '';const used=Math.max(0,Math.min(100,w.usedPercent));return `<div class="usage-card"><div class="usage-top"><h3>${esc(label)}</h3>${badge(usage.stale?'cached':'available')}</div><div class="usage-percent">${w.remainingPercent.toFixed(1)}% <span>remaining</span></div><div class="progress-track" style="margin:13px 0 7px"><div class="progress-fill" style="width:${used.toFixed(2)}%"></div></div><div class="usage-values"><span>${used.toFixed(1)}% used</span><span>${w.remainingPercent.toFixed(1)}% free</span></div><div class="usage-note">${w.resetsAt?'Resets '+esc(dateTime(w.resetsAt)):'Reset time not reported'}${usage.observedAt?' · Updated '+esc(relative(usage.observedAt)):''}</div></div>`};return `<div class="usage-layout">${format(usage.weekly,'Weekly allowance')}${format(usage.shortWindow,'Short window')}</div>`}
function rowProjects(list){return list.map(p=>{const jobs=state.jobs.filter(j=>j.projectId===p.id);return `<tr><td><strong>${esc(p.name)}</strong><small class="mono">${esc(shorten(p.path,60))}</small></td><td>${badge(p.status||'active')}</td><td class="mono">${esc(p.baseBranch)}</td><td>${jobs.length}</td><td>${p.maxParallel}</td><td><div class="tiny-actions"><button class="tiny-button" data-inspect="project:${p.id}">Inspect</button><button class="tiny-button" data-edit="project:${p.id}">Configure</button><button class="tiny-button" data-act="provider-config" data-id="${p.id}">Providers</button><button class="tiny-button" data-act="analysis-run" data-id="${p.id}">Review</button></div></td></tr>`}).join('')}
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
function renderGoals(){const items=filtered(state.goals,['objective']);const map=Object.fromEntries(state.projects.map(p=>[p.id,p.name]));return header('Goals','Define outcomes, acceptance criteria and token budgets.','create-goal','Create goal')+`<section class="card">${toolbar('goals',['draft','active','paused','stopped','completed','archived'])}<div class="cards-list" style="padding:18px">${items.length?items.map(g=>{const jobs=state.jobs.filter(j=>j.goalId===g.id);const done=jobs.filter(j=>j.integrated).length;return `<div class="entity-card"><div class="entity-top"><span class="entity-avatar">${ico('target')}</span><div><div class="entity-name">${esc(shorten(g.objective,85))}</div><div class="entity-sub">${esc(map[g.projectId]||'Unknown project')}</div></div></div><div class="entity-body">${g.acceptanceCriteria.length} acceptance criteria · ${g.tokenBudget.toLocaleString()} tokens<br>${jobs.length} work packages · ${done} integrated</div>${jobs.length?`<div class="progress-track"><div class="progress-fill" style="width:${Math.round(done/jobs.length*100)}%"></div></div>`:''}<div class="entity-foot">${badge(g.state)}<div class="tiny-actions"><button class="tiny-button" data-inspect="goal:${g.id}">Inspect</button><button class="tiny-button" data-edit="goal:${g.id}">Edit</button><button class="tiny-button" data-view="plans">Plans</button></div></div></div>`}).join(''):empty('target','No matching goals','Create a goal and set clear success criteria.','create-goal','Create goal')}</div></section>`}
function renderPlans(){let cards=state.goals.map(g=>{const p=plansCache.get(g.id);return `<div class="entity-card"><div class="entity-top"><span class="entity-avatar">${ico('route')}</span><div><div class="entity-name">${esc(shorten(g.objective,75))}</div><div class="entity-sub">${p?`${p.packages.length} work packages · Base ${shorten(p.baseCommit,10)}`:'No plan created yet'}</div></div></div><div class="entity-body">${p?`Dependencies and execution groups are validated before adoption.`:'Create a manual plan or let the planning agent propose work packages.'}</div><div class="entity-foot">${badge(g.state)}<div class="tiny-actions"><button class="tiny-button" data-act="plan-view" data-id="${g.id}" ${!p?'disabled':''}>View</button><button class="tiny-button" data-act="plan-edit" data-id="${g.id}" ${g.state!=='draft'?'disabled':''}>Edit</button><button class="tiny-button" data-act="plan-generate" data-id="${g.id}" ${g.state!=='draft'?'disabled':''}>Generate</button>${p&&g.state==='draft'?`<button class="tiny-button" data-act="plan-adopt" data-id="${g.id}">Adopt</button>`:''}${g.state==='active'?`<button class="tiny-button" data-act="queue-run">Run</button><button class="tiny-button" data-control="goal:${g.id}:pause">Pause</button><button class="tiny-button" data-control="goal:${g.id}:stop">Stop</button>`:''}${['paused','stopped'].includes(g.state)?`<button class="tiny-button" data-control="goal:${g.id}:activate">Resume</button>`:''}</div></div></div>`}).join('');return header('Plans','Create, generate, edit, validate and adopt dependency-aware execution plans.','create-goal','Create goal')+`<div class="cards-list">${cards||empty('route','No plans available','Create a goal to design its execution plan.','create-goal','Create goal')}</div>`}
function renderJobs(){const items=filtered(state.jobs,['title','description','state','risk']);return header('Jobs','Inspect execution, manage priority and safely control work packages.','create-job','Create job')+`<section class="card">${toolbar('jobs',['queued','preparing','building','gating','reviewing','reworking','awaiting_approval','pr_ready','completed_local','integrated','paused','stopped','failed','blocked'])}${items.length?table(['Work package','Project','State','Risk','Priority',''],rowJobs(items.slice().reverse())):empty('layers','No matching jobs','Create a work package or adopt an approved plan.','create-job','Create job')}</section>`}
function renderWorkers(){const w=state.workers;return header('Workers','Monitor health and control remote-worker admission. Drain/disable never force-kills a running process.','create-worker','Register worker')+`<div class="grid metrics-grid">${metric('Registered',w.length,'Known workers','cpu')}${metric('Online',w.filter(workerActive).length,'Heartbeat < 20 seconds','activity')}${metric('Draining',w.filter(x=>x.draining).length,'No new work assigned','pause')}${metric('Available',w.filter(x=>x.enabled&&!x.draining&&workerActive(x)).length,'Eligible for claims','check')}</div><div class="cards-list">${w.length?w.map(x=>`<div class="entity-card"><div class="entity-top"><span class="entity-avatar">${ico('cpu')}</span><div><div class="entity-name">${esc(x.id)}</div><div class="entity-sub">${esc(x.platform)}</div></div></div><div class="entity-body"><div class="worker-heartbeat"><span class="live-dot ${workerActive(x)?'':'offline'}"></span>${workerActive(x)?'Live heartbeat':'Heartbeat unavailable'} · ${esc(relative(x.heartbeat))}</div><p>Admission: ${x.enabled?(x.draining?'Draining':'Enabled'):'Disabled'}</p></div><div class="entity-foot">${badge(!x.enabled?'disabled':x.draining?'draining':workerActive(x)?'online':'offline')}<div class="tiny-actions"><button class="tiny-button" data-inspect="worker:${encodeURIComponent(x.id)}">Inspect</button><button class="tiny-button" data-act="worker-edit" data-id="${encodeURIComponent(x.id)}">Edit</button><button class="tiny-button" data-control="worker:${encodeURIComponent(x.id)}:${managedWorkers.includes(x.id)?'stop':'start'}">${managedWorkers.includes(x.id)?'Stop local':'Start local'}</button><button class="tiny-button" data-control="worker:${encodeURIComponent(x.id)}:${x.draining||!x.enabled?'enable':'drain'}">${x.draining||!x.enabled?'Enable':'Drain'}</button><button class="tiny-button" data-control="worker:${encodeURIComponent(x.id)}:disable" ${!x.enabled?'disabled':''}>Disable</button></div></div></div>`).join(''):empty('cpu','No registered workers','Register a remote worker, then start it with the same ID to track its heartbeat.','create-worker','Register worker')}</div>`}
function renderQueue(){const queued=state.jobs.filter(j=>j.state==='queued');const active=state.jobs.filter(j=>['preparing','building','gating','reviewing','reworking','awaiting_approval'].includes(j.state));const waiting=queued.filter(j=>j.dependsOn.some(dep=>!state.jobs.some(o=>o.id===dep&&o.integrated)));return header('Queue & scheduling','Control admission, prioritize work and inspect dependency blockers.')+`<div class="overview-hero"><div class="overview-hero-left"><div class="hero-orb">${ico('queue')}</div><div><h2>${state.paused?'Queue paused':'Scheduler ready'}</h2><p>${queued.length} queued · ${active.length} active · ${waiting.length} waiting for dependencies</p></div></div><div class="queue-controls"><button class="button button-primary" data-act="queue-run">${ico('play')} Run</button><button class="button button-muted" data-act="queue-pause">${ico('pause')} Pause</button><button class="button button-muted" data-act="queue-resume">${ico('refresh')} Resume</button><button class="button button-danger" data-act="queue-stop">${ico('stop')} Stop</button></div></div><div class="grid metrics-grid">${metric('Ready or queued',queued.length,'Eligible after constraints','layers')}${metric('Active',active.length,'Working or validating','activity')}${metric('Waiting dependencies',waiting.length,'Must integrate predecessors','route')}${metric('Global concurrency',settings.maxParallel??'—','Configured worker slots','cpu')}</div>${card('Work schedule','Prioritized by eligibility and conflict',queued.length?table(['Work package','Project','State','Risk','Priority',''],rowJobs(queued.slice().sort((a,b)=>b.priority-a.priority))):empty('check','Queue is empty','No pending jobs. Add a job or adopt a plan.','create-job','Add job'))}<div style="margin-top:17px">${card('In flight','Active jobs continue during queue pause',active.length?table(['Work package','Project','State','Risk','Priority',''],rowJobs(active)):empty('activity','Nothing running','Start the queue when jobs are ready.'))}</div>`}
function renderEvents(){return header('Activity & audit trail','Recent persisted lifecycle events, decisions and operational changes.')+card('Event stream','Most recent events appear first',eventItems(state.events,250))}
async function refreshProviderCapacity(){
 const projectId=capacityProjectId||state.projects[0]?.id;
 if(!projectId||!token)return;
 try{providerCapacityCache[projectId]=await api('projects/'+projectId+'/provider-capacity')}
 catch(error){providerCapacityCache[projectId]={error:error.message}}
 if(view==='usage')render();
}
async function loadProjectAnalysis(){
 const projectId=analysisProjectId||state.projects[0]?.id;
 if(!projectId||!token)return;
 try{analysisReports[projectId]=await api('projects/'+projectId+'/analysis')}
 catch{analysisReports[projectId]=null}
 if(view==='analysis')render();
}
function renderAnalysis(){
 const projectId=analysisProjectId||state.projects[0]?.id;
 if(!projectId)return header('Project Review','Analyze a repository before planning goals.')+empty('folder','No projects registered','Register a project to start a review.','create-project','Register project');
 const picker='<div class="field"><label for="analysis-project">Project</label><select id="analysis-project">'+state.projects.map(p=>'<option value="'+esc(p.id)+'" '+(p.id===projectId?'selected':'')+'>'+esc(p.name)+'</option>').join('')+'</select></div>';
 const report=analysisReports[projectId];
 const controls='<div class="queue-controls"><button class="button button-primary" data-act="analysis-run" data-id="'+esc(projectId)+'" '+(analysisBusy?'disabled':'')+'>'+ico('shield')+(analysisBusy?' Reviewing...':' Start read-only review')+'</button><button class="button button-muted" data-act="provider-config" data-id="'+esc(projectId)+'">Provider settings</button></div>';
 let content=report===undefined?'<p class="subtext">Load a saved review or start a new one.</p>':!report?empty('shield','No review recorded','Start a read-only repository analysis to propose actionable goals.'):('<div class="job-info-grid"><div class="kv"><small>Reviewed commit</small><strong>'+esc(shorten(report.baseCommit,18))+'</strong></div><div class="kv"><small>Provider</small><strong>'+esc(report.providerId)+' · '+esc(report.model)+'</strong></div><div class="kv"><small>Recorded</small><strong>'+esc(dateTime(report.createdAt))+'</strong></div></div><p class="subtext">'+esc(report.summary)+'</p><p class="subtext">All findings are suggestions and require independent review before goal creation. An existing evidence file is not proof that the diagnosis is correct.</p>');
 if(report?.findings?.length)content+=report.findings.map((x,i)=>'<div class="entity-card" style="margin-top:12px"><div class="entity-top"><span class="entity-avatar">'+ico('shield')+'</span><div><div class="entity-name">'+esc(x.title)+'</div><div class="entity-sub">'+badge(x.severity)+' · '+(x.evidenceVerified?'Source path exists':'Evidence not verified')+'</div></div></div><div class="entity-body"><p>'+esc(x.explanation)+'</p><p class="subtext">Evidence: '+esc(x.evidencePath||'Not supplied')+'</p><p class="subtext">Suggested goal: '+esc(x.proposedGoal)+'</p></div><div class="entity-foot"><button class="button button-muted" data-act="analysis-goal" data-id="'+esc(projectId)+'" data-index="'+i+'">Prepare goal draft</button></div></div>').join('');
 return header('Project Review','Inspect evidence, challenge findings and prepare new goals. No automatic execution.')+picker+controls+'<section class="card" style="margin-top:18px"><div class="card-head"><div><h2>Review findings</h2><p>Commit-bound, provider-generated proposals</p></div></div><div class="card-body">'+content+'</div></section>';
}
function renderUsage(){
 const projectId=capacityProjectId||state.projects[0]?.id;
 const title=header('Provider capacity & budgets',
  'Controller allocations and observed consumption are separate from provider account allowances.');
 if(!projectId)return title+(usage?usageCards():'')+
  empty('folder','No projects yet','Register a project to configure budgets.','create-project','Add project');
 const options=state.projects.map(p=>'<option value="'+esc(p.id)+'" '+
  (p.id===projectId?'selected':'')+'>'+esc(p.name)+'</option>').join('');
 const picker='<div class="field"><label for="capacity-project">Project</label>'+
  '<select id="capacity-project">'+options+'</select></div>';
 const data=providerCapacityCache[projectId], nf=n=>Number(n||0).toLocaleString();
 let body='';
 if(!data)body='<p class="subtext">Loading per-provider usage...</p>';
 else if(!Array.isArray(data))body='<p role="alert" class="subtext">Capacity unavailable: '+
  esc(data.error||'Unknown error')+'</p>';
 else body=data.map(p=>{
  const rows=(p.breakdown||[]).map(m=>{
   const cost=m.reportedCostMicros==null?'Not reported':
    '$'+(Number(m.reportedCostMicros)/1000000).toFixed(4);
   return '<tr><td>'+esc(m.model)+'</td><td>'+esc(m.role)+'</td><td>'+
    nf(m.inputTokens)+'</td><td>'+nf(m.outputTokens)+'</td><td>'+esc(cost)+'</td></tr>';
  }).join('');
  const cap=p.tokenLimit==null?'Not configured':nf(p.tokenLimit);
  const available=p.remainingTokens==null?'Unknown / uncapped':nf(p.remainingTokens);
  const metrics='<div class="budget-breakdown"><div><small>Configured local limit</small><strong>'+
   cap+'</strong></div><div><small>Observed consumption</small><strong>'+
   nf(p.consumedTokens)+'</strong></div><div><small>Reserved / unsettled</small><strong>'+
   nf(p.outstandingTokens)+'</strong></div><div><small>Local remaining</small><strong>'+
   available+'</strong></div></div>';
  const actions='<button class="button button-muted" data-act="provider-budget-edit" data-id="'+
   esc(projectId)+'" data-index="'+esc(p.providerId)+'">Edit budget</button>';
  const tableBody=rows?table(['Model','Role','Input','Output','Reported USD cost'],rows):
   '<p class="subtext">No metered executions recorded.</p>';
  const allowance=p.providerId==='codex'&&usage?
   'Codex account allowance shown separately below':'Account allowance not available';
  return '<section class="card" style="margin-top:14px"><div class="card-head"><div>'+
   '<h2>'+esc(p.providerId)+'</h2><p>All models and roles in this project</p></div>'+
   actions+'</div><div class="card-body">'+metrics+'<p class="subtext">'+
   esc(allowance)+'. Local budgets do not establish account entitlements.</p>'+
   tableBody+'</div></section>';
 }).join('');
 const controls='<div class="queue-controls">'+
  '<button class="button button-muted" data-act="provider-config" data-id="'+
  esc(projectId)+'">Configure provider roles</button>'+
  '<button class="button button-muted" data-act="provider-refresh">Refresh usage</button>'+
  '<button class="button button-muted" data-act="provider-held" data-id="'+
  esc(projectId)+'">Reconcile held usage</button></div>';
 const allowances=usage?'<div style="margin-top:18px"><h2>Codex account allowances</h2>'+
  usageCards()+'</div>':
  '<p class="subtext">No account allowance report is currently available. '+
  'This does not mean zero remaining quota.</p>';
 return title+picker+controls+body+allowances;
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

function render(){if(!token){$('main').innerHTML=`<div class="loading-screen"><div style="text-align:center">${ico('shield')}<h2>Connect to Mission Control</h2><p class="muted">Enter the API token to manage your Factory workspace.</p><button class="button button-primary" data-act="open-connection">Connect</button></div></div>`;return}
const renders={overview:renderOverview,projects:renderProjects,analysis:renderAnalysis,goals:renderGoals,plans:renderPlans,workers:renderWorkers,jobs:renderJobs,queue:renderQueue,recovery:renderRecovery,events:renderEvents,usage:renderUsage,settings:renderSettings};$('main').innerHTML=(renders[view]||renderOverview)();
 for(const name of ['projects','goals','jobs','workers'])document.querySelectorAll(`[data-count="${name}"]`).forEach(el=>el.textContent=(state[name]||[]).length||'');
  document.querySelectorAll('[data-count="recovery"]').forEach(el=>el.textContent=recoveryCandidates().length||'');
 const cap=$('capacity-project');if(cap)cap.addEventListener('change',e=>{capacityProjectId=e.target.value;render();refreshProviderCapacity()});
 const review=$('analysis-project');if(review)review.addEventListener('change',e=>{analysisProjectId=e.target.value;render();loadProjectAnalysis()});
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
function formGoal(id,draft){const g=state.goals.find(g=>g.id===id);if(!g&&!state.projects.length){notify('Register a project first',true);return}let v=g||draft||{projectId:state.projects[0].id,objective:'',acceptanceCriteria:[],tokenBudget:100000};const html=field('projectId','Project',v.projectId,'select',projectOptions())+field('objective','Outcome / objective',v.objective,'textarea')+field('acceptanceCriteria','Acceptance criteria (one per line)',v.acceptanceCriteria.join('\n'),'textarea')+field('tokenBudget','Token budget',v.tokenBudget,'number');openForm(g?'Edit goal':'Create goal','Describe a measurable outcome and its success criteria.',html,async f=>{let x=values(f);await mutate(g?`goals/${g.id}`:'goals',g?'PUT':'POST',{projectId:x.projectId,objective:x.objective,acceptanceCriteria:lines(x.acceptanceCriteria),tokenBudget:Number(x.tokenBudget)})},g?'Update goal':'Create goal')}
function formJob(id){const j=state.jobs.find(j=>j.id===id);if(!j&&!state.projects.length){notify('Register a project first',true);return}const v=j||{projectId:state.projects[0].id,goalId:'',title:'',description:'',mode:'local',risk:'medium',priority:50,dependsOn:[],expectedPaths:[]};let html=field('projectId','Project',v.projectId,'select',projectOptions())+field('goalId','Linked goal (optional)',v.goalId||'','select',goalOptions())+field('title','Title',v.title)+field('description','Work package description',v.description,'textarea')+`<div class="field-row">${field('mode','Execution',v.mode,'select',[['local','Local'],['remote','Remote']])}${field('risk','Risk',v.risk,'select',[['low','Low'],['medium','Medium'],['high','High']])}</div>`+field('priority','Priority (0-100)',v.priority,'number')+field('expectedPaths','Expected paths (one per line)',v.expectedPaths.join('\n'),'textarea')+field('dependsOn','Dependencies (job IDs, one per line)',v.dependsOn.join('\n'),'textarea');
 openForm(j?'Edit job':'Create job','Execution starts only when dependencies and policies allow it.',html,async f=>{const x=values(f);await mutate(j?`jobs/${j.id}`:'jobs',j?'PUT':'POST',{projectId:x.projectId,goalId:x.goalId||null,title:x.title,description:x.description,mode:x.mode,risk:x.risk,priority:Number(x.priority),expectedPaths:lines(x.expectedPaths),dependsOn:lines(x.dependsOn)})},j?'Save job':'Queue job')}
async function configureProviders(projectId){
 const current=await api('projects/'+projectId+'/providers');
 const roles=['planning','analysis','implementation','review','review:code','review:architecture','review:security','review:ux'];
 const choices=role=>[['inherit','Project default / inherited'],['codex','Codex'],...(role==='implementation'?[]:[['claude','Claude Code (read-only)']])];
 const html=roles.map((role,i)=>{
  const currentRoute=current.find(x=>x.role===role);
  return '<div class="field-row">'+field('route'+i,role+' provider',currentRoute?.providerId||'inherit','select',choices(role))+field('model'+i,'Model override',currentRoute?.model||'')+'</div>';
 }).join('')+'<p class="subtext">Unassigned roles use Codex by default. Specialized reviews inherit the general review route. Claude Code cannot run write-enabled implementations or remote workers.</p>';
 openForm('Provider routing','Select providers and models independently within this project. Provider credentials remain in their own native CLIs.',html,async f=>{
  const x=values(f),routes=[];
  roles.forEach((role,i)=>{const providerId=x['route'+i],model=(x['model'+i]||'').trim();
   if(providerId==='inherit'){if(model)throw Error('Choose a provider for model override: '+role);return}
   routes.push({projectId,role,providerId,model});
  });
  await mutate('projects/'+projectId+'/providers','PUT',{routes});
 },'Save provider routes');
}
async function editProviderBudget(projectId,providerId){
 const data=await api('projects/'+projectId+'/provider-capacity');
 const current=data.find(x=>x.providerId===providerId);
 if(!current)throw Error('Unknown provider');
 const html=field('tokenLimit','Provider token limit (0 = no local cap)',current.tokenLimit||0,'number')+
  '<p class="subtext">Budget covers all models and roles for this provider in the selected project. Active/held reservations count against it. Changing a local cap does not change the provider account allowance.</p>';
 openForm('Provider budget · '+providerId,'A zero limit removes the Factory cap; it does not confirm unlimited provider capacity.',html,async f=>{
  const value=Number(values(f).tokenLimit);
  if(!Number.isSafeInteger(value)||value<0)throw Error('Enter a nonnegative integer token limit');
  await mutate('projects/'+projectId+'/provider-budgets/'+encodeURIComponent(providerId),'PUT',{tokenLimit:value});
  await refreshProviderCapacity();
 },'Save provider limit');
}
async function editHeldProviderUsage(projectId){
 const turns=await api('projects/'+projectId+'/provider-held');
 if(!turns.length){notify('No held provider reservations require reconciliation');return}
 const first=turns[0];
 const opts=turns.map(t=>[t.id,t.providerId+' · '+t.role+' · '+(t.jobId||'project analysis')+' · '+t.reservedTokens+' reserved']);
 const html=field('turnId','Held provider turn',first.id,'select',opts)+
  field('finalInputTokens','Independently verified final input tokens',first.inputTokens,'number')+
  field('finalOutputTokens','Independently verified final output tokens',first.outputTokens,'number')+
  field('reason','Source of verification and reconciliation justification','','textarea')+
  field('reviewed','I independently verified the final provider usage',false,'checkbox')+
  '<p class="subtext">This operation releases unused reserved capacity. Use final counts for the selected turn, not your account allowance. If independent evidence is unavailable, leave the reservation held.</p>';
 openForm('Reconcile held provider usage','Administrative action requiring independent evidence. Tokens cannot be reduced below previously observed values.',html,async form=>{
  const x=values(form),input=Number(x.finalInputTokens),output=Number(x.finalOutputTokens);
  if(!form.elements.reviewed.checked||x.reason.trim().length<10||
      !Number.isSafeInteger(input)||!Number.isSafeInteger(output)||input<0||output<0)
   throw Error('Verified nonnegative integer totals and a justification of at least 10 characters are required');
  if(!confirm('Confirm independently verified final provider usage and release the unspent reservation?'))return;
  await mutate('projects/'+projectId+'/provider-held/'+encodeURIComponent(x.turnId)+'/reconcile',
   'POST',{finalInputTokens:input,finalOutputTokens:output,reviewed:true,reason:x.reason.trim()});
  await refreshProviderCapacity();
 },'Reconcile verified usage');
}
async function startProjectAnalysis(projectId){
 if(analysisBusy)return;
 analysisBusy=true;render();
 try{
  const report=await api('projects/'+projectId+'/analysis','POST',{});
  analysisReports[projectId]=report;
  notify('Project analysis recorded; findings require review');
 }finally{analysisBusy=false;render()}
}
function proposeGoalFromAnalysis(projectId,index){
 const item=analysisReports[projectId]?.findings?.[Number(index)];
 if(!item)throw Error('Finding is unavailable; reload the project review');
 formGoal(undefined,{projectId,objective:item.proposedGoal,acceptanceCriteria:item.acceptanceCriteria||[],tokenBudget:100000});
}
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
const plansCache=new Map(); let managedWorkers=[];async function loadPlans(){if(!token)return;try{const plans=await api('plans');plansCache.clear();for(const p of plans)if(p.plan)plansCache.set(p.id,p.plan);if(view==='plans')render()}catch{}}
function planEditor(goalId){const g=state.goals.find(g=>g.id===goalId);if(!g)return;const existing=plansCache.get(goalId);planGoal=goalId;planDraft=structuredClone(existing||{goalId,projectId:g.projectId,baseCommit:'',packages:[]});renderPlanEditor()}
function renderPlanEditor(){if(!planDraft)return;const html=`<div class="card" style="margin-top:17px"><div class="card-head"><div><h2>Work packages</h2><p>Dependencies use zero-based package indexes; cycles are rejected by the server.</p></div><button class="button button-muted" data-act="package-add">${ico('plus')} Add package</button></div><div class="card-body"><div class="plan-packages">${planDraft.packages.length?planDraft.packages.map((p,i)=>`<div class="package-row"><span class="package-num">${i}</span><div><strong>${esc(p.title)}</strong><small>${esc(shorten(p.description,100))} · Priority ${p.priority} · ${esc(p.risk)} · Depends on ${esc(p.dependsOn.join(', ')||'none')}</small></div><div class="tiny-actions"><button class="tiny-button" data-act="package-edit" data-index="${i}">Edit</button><button class="tiny-button" data-act="package-remove" data-index="${i}">Remove</button></div></div>`).join(''):empty('route','No packages yet','Add work packages and dependencies before saving.')}</div><div class="head-actions" style="margin-top:20px;justify-content:flex-end"><button class="button button-muted" data-act="plan-close">Cancel</button><button class="button button-primary" data-act="plan-save">${ico('check')} Save plan</button></div></div></div>`;$('inspector-title').textContent='Plan Designer';$('inspector-body').innerHTML=`<p class="muted">${esc(gName(planGoal))}</p>${html}`;showInspector()}
const gName=id=>state.goals.find(g=>g.id===id)?.objective||'';
function packageForm(i){const p=planDraft.packages[i]||{title:'',description:'',risk:'medium',priority:50,files:[],dependsOn:[]};let html=field('title','Package title',p.title)+field('description','Description / acceptance criteria',p.description,'textarea')+`<div class="field-row">${field('risk','Risk',p.risk,'select',[['low','Low'],['medium','Medium'],['high','High']])}${field('priority','Priority',p.priority,'number')}</div>`+field('files','Expected file paths, one per line',p.files.join('\n'),'textarea')+field('dependsOn','Dependency indexes, comma-separated',p.dependsOn.join(','));openForm(i===planDraft.packages.length?'Add work package':'Edit work package','Keep packages independently executable where possible.',html,async f=>{const x=values(f);const item={title:x.title,description:x.description,risk:x.risk,priority:Number(x.priority),files:lines(x.files),dependsOn:x.dependsOn.split(',').map(x=>x.trim()).filter(Boolean).map(Number)};if(item.dependsOn.some(n=>!Number.isInteger(n)||n<0||n===i||n>=planDraft.packages.length))throw Error('Invalid dependency indexes');planDraft.packages[i]=item;renderPlanEditor()})}

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
  const linkedGoal=state.goals.find(g=>g.id===goalId);
  const providerBudget=linkedGoal?await api('projects/'+linkedGoal.projectId+'/provider-capacity').catch(()=>null):null;
  if(inspectorKey!=='goal:'+goalId||!$('inspector-goal-budget'))return;
  const nf=x=>Number(x||0).toLocaleString();
  const rows=budget.reservations||[];
  const active=rows.filter(r=>r.state==='active').reduce((a,r)=>a+Number(r.outstanding||0),0);
  const held=rows.filter(r=>r.state==='held').reduce((a,r)=>a+Number(r.outstanding||0),0);
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
   ${held>0?`<button class="button button-muted" data-act="budget-reconcile" data-id="${goalId}">Review held usage</button>`:''}
   <h3>Provider capacity for this goal's project</h3>
   <p class="subtext">These are project-wide controller budgets shared by all goals and roles. They are not provider account allowances, nor a forecast of this goal's future token consumption.</p>
   ${providerBudget?table(['Provider','Local cap','Consumed','Reserved / held','Locally available'],
    providerBudget.map(p=>'<tr><td>'+esc(p.providerId)+'</td><td>'+(p.tokenLimit==null?'Not set':nf(p.tokenLimit))+'</td><td>'+nf(p.consumedTokens)+'</td><td>'+nf(p.outstandingTokens)+'</td><td>'+(p.remainingTokens==null?'Unknown / uncapped':nf(p.remainingTokens))+'</td></tr>').join('')):
    '<p class="subtext">Provider budget data is currently unavailable.</p>'}
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
 const held=(budget.reservations||[]).filter(r=>r.state==='held');
 if(!held.length){notify('No held reservations to reconcile');return}
 const options=held.map(r=>[r.id,(state.jobs.find(j=>j.id===r.jobId)?.title||r.jobId)+' · '+r.outstanding+' outstanding']);
 const first=held[0],job=state.jobs.find(j=>j.id===first.jobId);
 openForm('Reconcile held usage','Administrator action. Only independently verified final provider usage can release unknown liabilities.',
  field('reservationId','Held reservation',first.id,'select',options)+
  field('finalTokensIn','Final input tokens',job?.tokensIn||0,'number')+
  field('finalTokensOut','Final output tokens',job?.tokensOut||0,'number')+
  field('reason','Evidence and review justification','','textarea')+
  field('reviewed','I independently verified the final usage',false,'checkbox'),
  async form=>{
   const x=values(form),input=Number(x.finalTokensIn),output=Number(x.finalTokensOut);
   if(!form.elements.reviewed.checked||x.reason.trim().length<10||
      !Number.isSafeInteger(input)||!Number.isSafeInteger(output)||input<0||output<0)
    throw Error('Verified nonnegative integer totals and a justification of at least 10 characters are required');
   if(!confirm('Release unused reserved capacity based on independently verified final usage? This action is recorded.'))return;
   await mutate(`goals/${goalId}/budget/reconcile`,'POST',{
     reservationId:x.reservationId,finalTokensIn:input,finalTokensOut:output,reviewed:true,reason:x.reason.trim()
   });
   await renderGoalBudget(goalId);
  },'Confirm reconciliation');
}

function showInspector(){inspector=true;$('inspector').classList.add('open');$('inspector').setAttribute('aria-hidden','false');$('inspector-backdrop').classList.add('open')}
function closeInspector(){inspectorKey=null;inspector=null;$('inspector').classList.remove('open');$('inspector').setAttribute('aria-hidden','true');$('inspector-backdrop').classList.remove('open')}
function inspectorPanel(title,body){inspectorKey=null;$('inspector-title').textContent=title;$('inspector-body').innerHTML=body;showInspector()}
function inspect(type,id){const entity=(type==='project'?state.projects:type==='goal'?state.goals:type==='worker'?state.workers:state.jobs).find(x=>(type==='worker'?x.id: x.id)===id);if(!entity)return;let body='';const kv=(label,v)=>`<div class="kv"><small>${esc(label)}</small><strong>${esc(v)}</strong></div>`;
 if(type==='job'){const j=entity;body=`${badge(j.state)}<div class="job-info-grid" style="margin-top:18px">${kv('Priority',j.priority)}${kv('Risk',j.risk)}${kv('Execution mode',j.mode)}${kv('Stage',j.stage)}${kv('Attempt',j.attempt)}${kv('Tokens',Number(j.tokensIn||0)+Number(j.tokensOut||0))}</div><h3>Description</h3><p class="subtext">${esc(j.description)}</p>${j.error?`<div class="badge danger">${esc(j.error)}</div>`:''}${kv('Branch',j.branch||'—')}${kv('Worktree',j.worktree||'—')}${kv('Head commit',j.headSha||'—')}${kv('Dependencies',j.dependsOn.join(', ')||'None')}${safeUrl(j.prUrl)?`<p><a href="${esc(safeUrl(j.prUrl))}" rel="noopener noreferrer" target="_blank">View pull request ↗</a></p>`:''}<hr class="panel-divider"><h3>Controls</h3><div class="queue-controls">${['queued'].includes(j.state)?`<button class="button button-primary" data-act="job-start" data-id="${j.id}" ${j.mode==='remote'?'disabled':''}>Run now</button><button class="button button-muted" data-control="job:${j.id}:pause">Pause</button><button class="button button-danger" data-control="job:${j.id}:stop">Stop</button>`:''}${['preparing','building','gating','reviewing','reworking','awaiting_approval'].includes(j.state)?`<button class="button button-muted" data-control="job:${j.id}:pause">Pause local</button><button class="button button-danger" data-control="job:${j.id}:stop">Stop local</button>`:''}${['paused','stopped','blocked','failed'].includes(j.state)?`<button class="button button-primary" data-control="job:${j.id}:resume">Review & requeue</button>`:''}${['pr_ready','completed_local'].includes(j.state)?`<button class="button button-muted" data-act="verify" data-id="${j.id}">Verify integration</button>`:''}</div>`;
 body+=jobInspectorDetails(j,kv);
  }else if(type==='project'){const p=entity;body=`${badge(p.status||'active')}<div class="job-info-grid" style="margin-top:18px">${kv('Base branch',p.baseBranch)}${kv('Concurrency',p.maxParallel)}</div>${kv('Path',p.path)}${kv('Origin',p.origin||'Local')}${kv('Checks',p.checks.length)}${kv('Protected paths',p.protectedPaths.join(', '))}<hr class="panel-divider"><div class="queue-controls"><button class="button button-muted" data-edit="project:${p.id}">Configure</button><button class="button button-muted" data-act="provider-config" data-id="${p.id}">Providers</button><button class="button button-muted" data-act="analysis-run" data-id="${p.id}">Run review</button>${['paused','disabled'].includes(p.status)?`<button class="button button-primary" data-control="project:${p.id}:activate">Activate</button>`:`<button class="button button-muted" data-control="project:${p.id}:pause">Pause</button><button class="button button-muted" data-control="project:${p.id}:disable">Disable</button>`}<button class="button button-danger" data-control="project:${p.id}:archive">Archive</button></div>`;
 }else if(type==='goal'){const g=entity;body=`${badge(g.state)}${kv('Objective',g.objective)}${kv('Token budget',g.tokenBudget.toLocaleString())}<h3>Acceptance criteria</h3><ul>${g.acceptanceCriteria.map(c=>`<li>${esc(c)}</li>`).join('')}</ul><hr class="panel-divider"><div class="queue-controls"><button class="button button-muted" data-edit="goal:${g.id}">Edit</button>${g.state==='draft'?`<button class="button button-primary" data-control="goal:${g.id}:activate">Activate</button>`:''}${['active'].includes(g.state)?`<button class="button button-muted" data-control="goal:${g.id}:pause">Pause</button><button class="button button-muted" data-control="goal:${g.id}:stop">Stop</button><button class="button button-muted" data-control="goal:${g.id}:complete">Complete</button>`:''}${['paused','stopped'].includes(g.state)?`<button class="button button-primary" data-control="goal:${g.id}:activate">Resume</button>`:''}<button class="button button-muted" data-control="goal:${g.id}:archive">Archive</button></div>`;
 body+='<hr class="panel-divider"><h3>Goal budget ledger</h3><div id="inspector-goal-budget" aria-live="polite"><p class="subtext">Loading controller budget data...</p></div>';
  }else{const w=entity;body=`${badge(!w.enabled?'disabled':w.draining?'draining':workerActive(w)?'online':'offline')}${kv('Platform',w.platform)}${kv('Last heartbeat',dateTime(w.heartbeat))}<p class="subtext">Draining prevents new claims; it does not terminate in-flight work.</p><div class="queue-controls"><button class="button button-muted" data-act="worker-edit" data-id="${encodeURIComponent(w.id)}">Configure</button><button class="button button-muted" data-control="worker:${encodeURIComponent(w.id)}:${managedWorkers.includes(w.id)?'stop':'start'}">${managedWorkers.includes(w.id)?'Stop local':'Start local'}</button><button class="button button-muted" data-control="worker:${encodeURIComponent(w.id)}:enable">Enable</button><button class="button button-muted" data-control="worker:${encodeURIComponent(w.id)}:drain">Drain</button><button class="button button-danger" data-control="worker:${encodeURIComponent(w.id)}:disable">Disable</button></div>`;}
 const events=state.events.filter(e=>e.jobId===id).slice(-15);if(events.length)body+=`<hr class="panel-divider"><h3>Job activity</h3>${eventItems(events,15)}`;
 inspectorPanel((type==='job'?entity.title:type==='goal'?shorten(entity.objective,40):type==='project'?entity.name:entity.id),body);
  inspectorKey=type+':'+id;
  if(type==='goal')renderGoalBudget(id);
  if(type==='job')renderJobQuality(id);
}
async function act(action,id,index){switch(action){case'provider-config':await configureProviders(id);break;case'provider-budget-edit':await editProviderBudget(id,index);break;case'provider-refresh':await refreshProviderCapacity();break;case'provider-held':await editHeldProviderUsage(id);break;case'analysis-run':analysisProjectId=id;if(view!=='analysis')nav('analysis');await startProjectAnalysis(id);break;case'analysis-goal':proposeGoalFromAnalysis(id,index);break;case'open-connection':$('connection-modal').showModal();break;case'create-project':formProject();break;case'create-goal':formGoal();break;case'create-job':formJob();break;case'create-worker':formWorker();break;case'worker-edit':formWorker(decodeURIComponent(id));break;case'edit-settings':formSettings();break;case'budget-refresh':await renderGoalBudget(id);break;case'budget-reconcile':await reconcileGoalBudget(id);break;case'quality-refresh':await renderJobQuality(id);break;
 case'queue-run':case'queue-pause':case'queue-resume':case'queue-stop':await runQueue(action.slice(6));break;
 case'verify':await verify(id);break;
 case'job-start':await mutate(`jobs/${id}/start`,'POST',{});break;
 case'plan-generate':await mutate(`plans/${id}/generate`,'POST',{});break;
 case'plan-edit':planEditor(id);break;
 case'plan-view':{const plan=plansCache.get(id);if(!plan)return;const groups=plan.packages.map((p,i)=>`<div class="package-row"><span class="package-num">${i}</span><div><strong>${esc(p.title)}</strong><small>${esc(p.description)} · Depends on ${p.dependsOn.join(', ')||'none'}</small></div></div>`).join('');inspectorPanel('Plan details',`<p class="subtext">${esc(gName(id))}</p><div class="plan-packages">${groups}</div>`);break;}
 case'plan-adopt':if(!confirm('Adopt the plan into executable work packages? This changes the goal from draft to active.'))return;await mutate(`plans/${id}/adopt`,'POST',{mode:'local'});break;
 case'plan-close':closeInspector();planDraft=null;break;
 case'package-add':packageForm(planDraft.packages.length);break;
 case'package-edit':packageForm(Number(index));break;
 case'package-remove':{const i=Number(index);if(planDraft.packages.some((p,n)=>n!==i&&p.dependsOn.includes(i))){notify('Remove dependent references first',true);return}planDraft.packages.splice(i,1);for(const p of planDraft.packages)p.dependsOn=p.dependsOn.map(n=>n>i?n-1:n);renderPlanEditor();break;}
 case'plan-save':if(!planDraft.packages.length){notify('Add at least one work package',true);return}await mutate(`plans/${planGoal}`,'PUT',planDraft);closeInspector();await loadPlans();break;
 }}
function showPalette(){const commands=[['Overview','overview','overview'],['Projects','projects','folder'],['Project Review','analysis','shield'],['Goals','goals','target'],['Plans','plans','route'],['Jobs','jobs','layers'],['Queue','queue','queue'],['Recovery','recovery','shield'],['Workers','workers','cpu'],['Activity','events','activity'],['Settings','settings','settings']];const q=$('palette-input').value.toLowerCase();$('palette-results').innerHTML=commands.filter(([a])=>a.toLowerCase().includes(q)).map(([title,to,i])=>`<button class="palette-entry" data-view="${to}">${ico(i)}${title}<small>Navigate</small></button>`).join('')||`<div class="empty">No matching commands</div>`}
document.addEventListener('click',async e=>{const btn=e.target.closest('[data-view],[data-act],[data-inspect],[data-edit],[data-control]');if(!btn)return;try{if(btn.dataset.view){nav(btn.dataset.view);if($('palette').open)$('palette').close();if(view==='plans')loadPlans();return}
 if(btn.dataset.inspect){const [type,id]=btn.dataset.inspect.split(':');inspect(type,decodeURIComponent(id));return}
 if(btn.dataset.edit){const [type,id]=btn.dataset.edit.split(':');if(type==='project')formProject(id);else if(type==='goal')formGoal(id);else if(type==='job')formJob(id);return}
 if(btn.dataset.control){const bits=btn.dataset.control.split(':');await confirmControl(bits[0],decodeURIComponent(bits[1]),bits[2]);return}
 if(btn.dataset.act)await act(btn.dataset.act,btn.dataset.id,btn.dataset.index)
 }catch(error){notify(error.message,true)}});
$('modal-form').addEventListener('submit',async e=>{e.preventDefault();const btn=$('modal-submit');btn.disabled=true;try{if(modalHandler)await modalHandler(e.target);$('form-modal').close()}catch(error){notify(error.message,true)}finally{btn.disabled=false}});
$('modal-close').addEventListener('click',()=>{$('form-modal').close()});$('modal-cancel').addEventListener('click',()=>$('form-modal').close());
$('connection-form').addEventListener('submit',async e=>{e.preventDefault();await connect($('api-token').value.trim())});
$('disconnect').addEventListener('click',()=>{$('connection-modal').close();connect('')});
document.querySelectorAll('[data-close]').forEach(el=>el.addEventListener('click',()=>$(el.dataset.close).close()));
$('open-connection').addEventListener('click',()=>{$('api-token').value=token;$('connection-modal').showModal()});
$('inspector-close').addEventListener('click',closeInspector);$('inspector-backdrop').addEventListener('click',closeInspector);
$('mobile-open').addEventListener('click',()=>{$('sidebar').classList.add('open');$('sidebar-backdrop').classList.add('open')});
$('mobile-close').addEventListener('click',()=>{$('sidebar').classList.remove('open');$('sidebar-backdrop').classList.remove('open')});
$('sidebar-backdrop').addEventListener('click',()=>{$('sidebar').classList.remove('open');$('sidebar-backdrop').classList.remove('open')});
$('top-create').addEventListener('click',()=>{const actions=[['Project','create-project'],['Goal','create-goal'],['Job','create-job'],['Worker','create-worker']];openForm('Create new','Choose the object to add to your Factory workspace.',`<div class="list-stack">${actions.map(([name,action])=>`<button type="button" class="palette-entry" data-act="${action}">${ico('plus')} New ${name}</button>`).join('')}</div>`,async()=>{},'Done')});
$('open-search').addEventListener('click',()=>{$('palette-input').value='';showPalette();$('palette').showModal();$('palette-input').focus()});
$('palette-input').addEventListener('input',showPalette);
document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){e.preventDefault();$('open-search').click()}if(e.key==='Escape'&&inspector)closeInspector()});
setInterval(statusFromHealth,1100);
setInterval(()=>{if(token&&Date.now()-lastStateUpdate>11000)refreshState()},10000);
setInterval(refreshUsage,60000);
setInterval(()=>{if(view==='plans'&&token)loadPlans();if(view==='workers'&&token)fetchManagedWorkers()},15000);
render();statusFromHealth();if(token)connect();else setTimeout(()=>$('connection-modal').showModal(),350);
