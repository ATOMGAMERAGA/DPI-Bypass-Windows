const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
function fixture(){
  let listener,resolveSample,pauses=0,nextTimer=1;
  const timers=new Map();
  const runtime={onMessage:{addListener:f=>listener=f},sendMessage:()=>new Promise(r=>resolveSample=r)};
  const window={};window.top=window;
  const document={visibilityState:'visible',querySelector:()=>null,querySelectorAll:()=>[{pause:()=>pauses++}]};
  vm.runInNewContext(fs.readFileSync(require.resolve('./content.js'),'utf8'),{window,document,location:{href:'https://www.youtube.com/watch?v=abcdefghijk'},
    chrome:{runtime},URL,performance:{now:()=>0},navigator:{},addEventListener(){},
    setTimeout:(f,ms)=>{const id=nextTimer++;timers.set(id,{f,ms});return id;},clearTimeout:id=>timers.delete(id),
    __dpiSocialDetector:{sample:()=>({sampledAt:0,position:0})}});
  return {listener,timers,get pauses(){return pauses;},resolve:()=>resolveSample({ok:true}),
    observe:owned=>listener({kind:'observe',documentToken:'token',binding:{targetUrl:'https://www.youtube.com/watch?v=abcdefghijk'},ownedRenderer:owned,leaseMs:20000})};
}
test('owned renderer lease pauses media even when native response never returns',()=>{
  const f=fixture();f.observe(true);const deadline=[...f.timers.values()].find(t=>t.ms===20000);
  assert.ok(deadline);deadline.f();assert.equal(f.pauses,1);
});
test('borrowed tab media is never paused by lease expiry or Stop',()=>{
  const f=fixture();f.observe(false);for(const t of [...f.timers.values()])t.f();f.listener({kind:'stop',pauseOwned:false});assert.equal(f.pauses,0);
});
test('late old sampling response cannot restart a stopped observer',async()=>{
  const f=fixture();f.observe(true);f.listener({kind:'stop',pauseOwned:true});f.resolve();await new Promise(r=>setImmediate(r));
  assert.equal(f.timers.size,0);assert.equal(f.pauses,1);
});
