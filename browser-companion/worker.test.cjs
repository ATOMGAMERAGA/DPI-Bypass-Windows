const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const {webcrypto}=require('node:crypto');
const url='https://www.youtube.com/watch?v=abcdefghijk';
function binding(patch={}) {return {schemaVersion:2,sessionId:'session',attemptId:'attempt',operationId:'operation',contextGeneration:1,
  modeGeneration:1,documentGeneration:1,goalHash:'goal',targetRef:'target',targetUrl:url,allowNavigation:true,
  goal:{platformId:'youtube',identityContext:'SAME_ACCOUNT',requiredCapabilities:['VIEW_SELECTED_CONTENT']},...patch};}
function deferred(){let resolve;const promise=new Promise(r=>resolve=r);return {promise,resolve};}
function harness() {
  let store={},listener,nextTab=10,now=10000;
  const events={},removed=[],navigated=[],scripts=[],sent=[],alarms=new Map(),timers=new Map(),tabs=new Map([[7,{id:7,url}]]);
  let timerId=0;
  let native=async m=>m.command==='social.bind'?{ok:true,binding:binding()}:{ok:true};
  const chrome={runtime:{id:'extension',onMessage:{addListener:f=>listener=f},sendNativeMessage:(_,m)=>native(m)},
    storage:{session:{get:async()=>structuredClone(store),set:async x=>Object.assign(store,structuredClone(x)),remove:async key=>{delete store[key];}}},
    tabs:{onUpdated:{addListener:f=>events.updated=f},onRemoved:{addListener:f=>events.removed=f},get:async id=>tabs.get(id),
      create:async options=>{const tab={id:nextTab++,...options};tabs.set(tab.id,tab);return tab;},
      update:async(id,options)=>{navigated.push({id,...options});Object.assign(tabs.get(id),options);return tabs.get(id);},
      remove:async id=>{removed.push(id);tabs.delete(id);},sendMessage:async(id,message)=>{sent.push({id,...message});return {ok:true};}},
    scripting:{executeScript:async details=>{scripts.push(details);return [];}},
    webRequest:{onBeforeRequest:{addListener:f=>events.before=f},onCompleted:{addListener:f=>events.completed=f}},
    alarms:{create:async(name,details)=>alarms.set(name,details),clear:async name=>alarms.delete(name),onAlarm:{addListener:f=>events.alarm=f}}};
  class Clock extends Date {static now(){return now;}}
  vm.runInNewContext(fs.readFileSync(require.resolve('./worker.js'),'utf8'),{chrome,URL,Set,Date:Clock,crypto:webcrypto,console,
    setTimeout:(f,ms)=>{const id=++timerId;timers.set(id,{f,ms});return id;},clearTimeout:id=>timers.delete(id)});
  const message=(m,sender={id:'extension'})=>new Promise(resolve=>listener(m,sender,resolve));
  const drain=async()=>{for(let i=0;i<12;i++)await new Promise(r=>setImmediate(r));};
  return {message,drain,events,removed,navigated,scripts,sent,alarms,timers,tabs,get state(){return structuredClone(store.social);},
    get lifecycle(){return structuredClone(store.socialLifecycle);},setNative:f=>native=f,setNow:n=>now=n,
    bind:()=>message({kind:'bind',tabId:7,navigate:true,sameAccount:true}),
    sample:()=>message({kind:'sample',documentToken:store.social.documentToken,signal:{}},
      {id:'extension',tab:{id:store.social.tabId},frameId:0,url,documentId:store.social.documentId})};
}
test('new mode/document generation in the same operation gets a fresh binding',async()=>{
  const h=harness();await h.bind();h.setNative(async m=>m.command==='social.bind'?{ok:true,binding:binding({modeGeneration:2,documentGeneration:2})}:{ok:false});
  await h.sample();assert.equal(h.state.binding.modeGeneration,2);assert.equal(h.state.binding.documentGeneration,2);
});
test('Stop invalidates delayed native work before it can navigate or resurrect state',async()=>{
  const h=harness();await h.bind();const gate=deferred();h.setNative(async m=>m.command==='social.observe'?gate.promise:{ok:true,binding:binding({operationId:'next'})});
  const sample=h.sample();await h.drain();const stop=h.message({kind:'stop'});gate.resolve({ok:true});await Promise.all([sample,stop]);
  assert.equal(h.state,undefined);assert.equal(h.navigated.length,1);assert.deepEqual(h.removed,[10]);
});
test('explicit Stop closes owned test tabs but never the selected borrowed tab',async()=>{
  const own=harness();await own.bind();await own.message({kind:'stop'});assert.deepEqual(own.removed,[10]);
  const borrowed=harness();await borrowed.message({kind:'bind',tabId:7,navigate:false,sameAccount:true});await borrowed.message({kind:'stop'});
  assert.deepEqual(borrowed.removed,[]);assert.equal(borrowed.tabs.has(7),true);
});
test('new sessions release their previous owned trial before opening another',async()=>{
  const h=harness();await h.bind();h.setNative(async()=>({ok:true,binding:binding({sessionId:'next-session'})}));await h.bind();
  assert.deepEqual(h.removed,[10]);assert.equal(h.state.tabId,11);assert.equal(h.tabs.size,2);
});
test('same-session rebind cannot reset renderer loads or restart the observation lease',async()=>{
  const h=harness();await h.bind();h.setNow(11000);await h.bind();assert.equal(h.state.loads,1);
  for(let i=2;i<=6;i++){h.setNative(async()=>({ok:true,binding:binding({operationId:'op'+i})}));await h.bind();}
  assert.equal(h.state.loads,6);await h.message({kind:'stop'});
  h.setNative(async()=>({ok:true,binding:binding({operationId:'op7'})}));assert.equal((await h.bind()).ok,false);
  assert.equal(h.navigated.length,6);
});
test('one-shot lease alarm releases a trial when desktop and content disappear',async()=>{
  const h=harness();await h.bind();assert.equal(h.alarms.size,1);const [name,alarm]=[...h.alarms][0];
  assert.ok(alarm.when<=30000);h.setNow(alarm.when+1);h.events.alarm({name});await h.drain();
  assert.equal(h.state,undefined);assert.deepEqual(h.removed,[10]);
});
test('desktop Stop releases the trial during the bounded local handshake',async()=>{
  const h=harness();await h.bind();h.setNative(async m=>m.command==='social.observe'?{ok:false,finished:true,keepActive:false}:
    {ok:false,reason:'NO_ACTIVE_ATTEMPT',finished:true,keepActive:false});await h.sample();
  assert.equal(h.state,undefined);assert.deepEqual(h.removed,[10]);
});
test('PASS pauses owned media and transfers the visible result to the user',async()=>{
  const h=harness();await h.bind();h.setNative(async()=>({ok:true,finished:true,keepActive:true}));await h.sample();
  assert.equal(h.state,undefined);assert.deepEqual(h.removed,[]);assert.equal(h.tabs.has(10),true);
  assert.ok(h.scripts.some(s=>typeof s.func==='function'));await h.message({kind:'stop'});assert.deepEqual(h.removed,[]);
});
test('transient desktop transition retains the bounded lease for the next content tick',async()=>{
  const h=harness();await h.bind();h.setNative(async m=>m.command==='social.observe'?{ok:false}:
    {ok:false,reason:'NO_ACTIVE_ATTEMPT',finished:false});
  assert.equal((await h.sample()).ok,true);assert.ok(h.state);assert.deepEqual(h.removed,[]);
});
test('a missing native response cannot hold the owned-tab cleanup queue indefinitely',async()=>{
  const h=harness();await h.bind();h.setNative(()=>new Promise(()=>{}));const sample=h.sample();await h.drain();
  const timeout=[...h.timers.values()].find(t=>t.ms<=4000);assert.ok(timeout);timeout.f();await sample;
  assert.equal(h.state,undefined);assert.deepEqual(h.removed,[10]);
});
test('late freshness events cannot recreate state after Stop',async()=>{
  const h=harness();await h.bind();h.events.before({tabId:10,timeStamp:10001,url,requestId:'request'});
  const stop=h.message({kind:'stop'});h.events.completed({tabId:10,timeStamp:10002,url,requestId:'request',type:'main_frame',documentId:'document'});
  await stop;await h.drain();assert.equal(h.state,undefined);assert.deepEqual(h.removed,[10]);
});
