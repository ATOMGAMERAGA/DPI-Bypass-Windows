const HOST='net.atom.dpibypass.social';
const ALLOWED=new Set(['www.youtube.com','m.youtube.com','youtube.com']);
const LEASE_ALARM='social-renderer-lease';
const BINDING_FIELDS=['sessionId','attemptId','operationId','contextGeneration','modeGeneration','documentGeneration','goalHash','targetRef'];
// A worker has one state writer. Stop/bind invalidate in-flight continuations immediately;
// the persisted generation also survives service-worker suspension/restart.
let serial=Promise.resolve(), epoch=0;
class Superseded extends Error {}
function enqueue(work) {const result=serial.then(work);serial=result.catch(()=>{});return result;}
function target(url) {try {const u=new URL(url);return u.protocol==='https:'&&!u.username&&!u.password&&!u.port&&ALLOWED.has(u.hostname)&&u.pathname==='/watch'&&/^[\w-]{11}$/.test(u.searchParams.get('v')||'')?u.origin+'/watch?v='+u.searchParams.get('v'):null;}catch{return null;}}
function sameBinding(a,b) {return !!a&&!!b&&BINDING_FIELDS.every(k=>a[k]===b[k]);}
function current(token) {if(token.epoch!==epoch) throw new Superseded();}
async function step(promise,token) {const result=await promise;current(token);return result;}
async function read(token) {
  const data=await step(chrome.storage.session.get(['social','socialLifecycle']),token);
  const lifecycle=data.socialLifecycle||{generation:0,budgets:{}};
  if(token.generation!==undefined&&token.generation!==lifecycle.generation) throw new Superseded();
  token.generation=lifecycle.generation;return {state:data.social,lifecycle};
}
async function save(state,lifecycle,token) {
  await read(token); // compare persisted generation before any state write
  await step(chrome.storage.session.set({social:state,socialLifecycle:lifecycle}),token);
}
async function native(message,token) {
  let timer;
  try {
    return await step(Promise.race([
      chrome.runtime.sendNativeMessage(HOST,message).catch(()=>({ok:false,reason:'HOST_UNAVAILABLE'})),
      new Promise(resolve=>{timer=setTimeout(()=>resolve({ok:false,reason:'HOST_TIMEOUT'}),4000);})
    ]),token);
  } finally {clearTimeout(timer);}
}
async function pauseOwned(state) {
  if(!state?.owned) return;
  await chrome.scripting.executeScript({target:{tabId:state.tabId},func:()=>{
    for(const video of document.querySelectorAll('video')) video.pause();
  }}).catch(()=>null);
}
async function release(keepActive=false) {
  const data=await chrome.storage.session.get(['social','socialLifecycle']);
  const state=data.social,lifecycle=data.socialLifecycle||{generation:0,budgets:{}};
  lifecycle.generation++;
  // Clear the durable lease before awaits that may fail because the tab disappeared.
  await chrome.storage.session.set({socialLifecycle:lifecycle});
  await chrome.storage.session.remove('social');
  await chrome.alarms.clear(LEASE_ALARM);
  if(!state) return;
  await chrome.tabs.sendMessage(state.tabId,{kind:'stop',pauseOwned:state.owned===true}).catch(()=>null);
  if(state.owned) {
    if(keepActive) await pauseOwned(state); // retained result becomes the user's tab
    else await chrome.tabs.remove(state.tabId).catch(()=>null);
  }
}
async function install(binding,state,lifecycle,token) {
  if(!binding||binding.schemaVersion!==2||binding.goal?.platformId!=='youtube'||!target(binding.targetUrl)) return false;
  if(state.navigate&&binding.allowNavigation===false) return false;
  if(state.binding&&sameBinding(state.binding,binding)) return true;
  const loads=lifecycle.budgets[binding.sessionId]||0;
  if(state.navigate&&loads>=6) return false;
  const tab=await step(chrome.tabs.get(state.tabId).catch(()=>null),token);
  if(!tab) return false;
  if(state.navigate&&!state.owned) {
    const created=await chrome.tabs.create({url:'about:blank',active:true});
    if(token.epoch!==epoch) {await chrome.tabs.remove(created.id).catch(()=>null);throw new Superseded();}
    state.tabId=created.id;state.owned=true;
  } else if(!state.navigate&&target(tab.url)!==target(binding.targetUrl)) return false;
  state.loads=loads+(state.navigate?1:0);lifecycle.budgets[binding.sessionId]=state.loads;
  state.binding=binding;state.documentToken=crypto.randomUUID();state.documentId=null;state.fresh=false;
  state.bindingStartedAt=Date.now();state.freshRequestId=null;
  state.deadline=Math.min(state.bindingStartedAt+20000,state.startedAt+120000);
  state.userBoundSession=state.sameAccount&&binding.goal.identityContext==='SAME_ACCOUNT';
  state.generation=lifecycle.generation;
  await save(state,lifecycle,token);
  await step(chrome.alarms.create(LEASE_ALARM,{when:state.deadline}),token);
  if(state.navigate) {
    await read(token);
    await step(chrome.tabs.update(state.tabId,{url:binding.targetUrl,active:true}),token);
  }
  return true;
}
async function observe(tabId,token) {
  const {state:s}=await read(token);
  if(!s||s.tabId!==tabId||!s.binding) return;
  if(Date.now()>=s.deadline) {await release();return;}
  await step(chrome.scripting.executeScript({target:{tabId},files:['detector.js','content.js']}).catch(()=>null),token);
  await read(token);
  await step(chrome.tabs.sendMessage(tabId,{kind:'observe',binding:{...s.binding,userBoundSession:s.userBoundSession},
    ownedRenderer:s.owned===true,leaseMs:Math.max(0,s.deadline-Date.now()),documentToken:s.documentToken}).catch(()=>null),token);
}
chrome.tabs.onUpdated.addListener((id,change)=>{if(change.status==='complete') {
  const token={epoch};enqueue(()=>observe(id,token));
}});
chrome.tabs.onRemoved.addListener(id=>{const token={epoch};enqueue(async()=>{const {state}=await read(token);if(state?.tabId===id) await release();});});
chrome.alarms.onAlarm.addListener(alarm=>{if(alarm.name===LEASE_ALARM) {
  const token={epoch};enqueue(async()=>{const {state}=await read(token);if(state&&Date.now()>=state.deadline) await release();});
}});
chrome.webRequest.onBeforeRequest.addListener(details=>{
  const token={epoch};enqueue(async()=>{
    const {state:s,lifecycle}=await read(token);
    if(!s?.binding||details.tabId!==s.tabId||details.timeStamp<s.bindingStartedAt||target(details.url)!==target(s.binding.targetUrl)) return;
    s.freshRequestId=details.requestId;s.fresh=false;s.documentId=null;await save(s,lifecycle,token);
  });
},{urls:['https://www.youtube.com/*','https://m.youtube.com/*'],types:['main_frame']});
chrome.webRequest.onCompleted.addListener(details=>{
  const token={epoch};enqueue(async()=>{
    const {state:s,lifecycle}=await read(token);
    if(!s?.binding||details.tabId!==s.tabId||details.fromCache||details.requestId!==s.freshRequestId||details.timeStamp<s.bindingStartedAt) return;
    if(details.type==='main_frame'&&target(details.url)===target(s.binding.targetUrl)) {
      s.fresh=true;s.documentId=details.documentId||null;await save(s,lifecycle,token);
    }
  });
},{urls:['https://www.youtube.com/*','https://m.youtube.com/*'],types:['main_frame']});
async function handle(message,sender,token) {
  if(message.kind==='bind'&&!sender.tab) {
    const response=await native({command:'social.bind'},token);
    if(!response.ok) return response;
    let {state:previous,lifecycle}=await read(token);
    if(previous&&previous.binding?.sessionId!==response.binding?.sessionId) {
      await release();({lifecycle}=await read({epoch:token.epoch}));token.generation=lifecycle.generation;previous=null;
    }
    const sameSession=previous?.binding?.sessionId===response.binding?.sessionId;
    const state={tabId:sameSession&&previous.owned?previous.tabId:message.tabId,sameAccount:message.sameAccount===true,
      navigate:message.navigate===true,owned:sameSession&&previous.owned===true,loads:0,
      startedAt:sameSession?previous.startedAt:Date.now(),binding:sameSession?previous.binding:null};
    // Retain the complete lease on an identical rebind, including loads and deadline.
    if(sameSession&&sameBinding(previous.binding,response.binding)) return {ok:true};
    lifecycle.generation++;token.generation=lifecycle.generation;
    await step(chrome.storage.session.set({socialLifecycle:lifecycle}),token);
    if(!await install(response.binding,state,lifecycle,token)) return {ok:false};
    if(!state.navigate) await observe(state.tabId,token);
    return {ok:true};
  }
  const {state:s,lifecycle}=await read(token);
  if(message.kind!=='sample'||!s?.binding||sender.frameId!==0||sender.tab?.id!==s.tabId
    ||message.documentToken!==s.documentToken||target(sender.url)!==target(s.binding.targetUrl)
    ||(s.documentId&&sender.documentId!==s.documentId)) return {ok:false};
  if(Date.now()>=s.deadline) {await release();return {ok:false};}
  const signal=message.signal;
  const permitted=['targetMatched','contentPresent','blockingGate','mediaProgress','documentVisible','mediaProgressSeconds','seeking','advertisement','preview',
    'needsInteraction','observedCapabilities','observedAtMonotonic','observationWindow','stableSamples','stableDurationMs','identityEvidence','cacheUncertain'];
  if(!signal||Object.keys(signal).some(k=>!permitted.includes(k))||JSON.stringify(signal).length>4096) return {ok:false};
  const b=s.binding;
  const observation={...Object.fromEntries(BINDING_FIELDS.map(k=>[k,b[k]])),...signal,schemaVersion:2,tabId:s.tabId,
    observerId:'youtube-dom',observerVersion:'1.0.0',evaluatorVersion:'1.0.0',actualSurface:'OFFICIAL_WEB',identityContext:b.goal.identityContext,
    identityEvidence:s.userBoundSession?signal.identityEvidence:'UNKNOWN',signalSource:'WEB_OBSERVER',routeProvenance:'EXACT_TARGET_PATH',
    freshEvidence:s.fresh&&!signal.cacheUncertain?'TRUE':'UNKNOWN',budgetAccuracy:'ESTIMATED',observerError:false,rendererLoadCount:s.loads,
    needsInteraction:b.goal.requiredCapabilities.includes('PLAY_MEDIA')&&signal.needsInteraction};
  const response=await native({command:'social.observe',observation},token);
  if(response.reason==='HOST_TIMEOUT'||response.reason==='HOST_UNAVAILABLE') {await release();return {ok:false};}
  if(response.finished) {await release(response.keepActive===true||(response.ok&&response.keepActive!==false));return {ok:false};}
  const next=await native({command:'social.bind'},token);
  if(!next.ok) {
    // During rollback/apply the desktop has no bindable document yet. Only the
    // existing content tick may retry, within this unchanged observation lease.
    if(next.finished===false&&next.reason==='NO_ACTIVE_ATTEMPT') return {ok:true};
    await release(next.keepActive===true);return {ok:false};
  }
  if(!sameBinding(next.binding,b)) {
    if(await install(next.binding,s,lifecycle,token)) {if(!s.navigate) await observe(s.tabId,token);}
    else await release();
    return {ok:false};
  }
  return response;
}
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if(sender.id!==chrome.runtime.id) {reply({ok:false});return false;}
  if(message.kind==='stop'&&!sender.tab) {
    ++epoch;enqueue(async()=>{await release();return {ok:true};}).then(reply).catch(()=>reply({ok:false}));return true;
  }
  if(message.kind==='bind'&&!sender.tab) ++epoch;
  const token={epoch};enqueue(()=>handle(message,sender,token)).then(reply).catch(()=>reply({ok:false}));return true;
});
