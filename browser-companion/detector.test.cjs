const {test}=require('node:test');
const assert=require('node:assert/strict');
const {sample}=require('./detector.js');
function fixture({gate=false,layout=true,id='abcdefghijk',ad=false,position=0,paused=false,seeking=false}={}) {
  const video={readyState:4,videoWidth:1920,currentTime:position,paused,seeking,ended:false,playbackRate:1};
  const player={classList:{contains:c=>c==='html5-video-player'||(c==='ad-showing'&&ad)},querySelector:()=>video};
  const watch={getAttribute:()=>id,hasAttribute:()=>true};
  const doc={visibilityState:'visible',querySelector:s=>s==='ytd-watch-flexy[video-id]'?(layout?watch:null):s==='#movie_player'?player:
    s.startsWith('ytd-player-error')&&gate?{getClientRects:()=>[{}]}:null};
  Object.defineProperty(doc,'body',{get(){throw Error('No body text inspection allowed');}});
  return {doc,video};
}
const url={href:'https://www.youtube.com/watch?v=abcdefghijk'};
test('recognized exact target yields structural content, no raw page access',()=>{
  const {doc}=fixture();const s=sample(doc,url,'abcdefghijk',null,0);
  assert.equal(s.targetMatched,'TRUE');assert.equal(s.contentPresent,'TRUE');assert.equal(s.blockingGate,'FALSE');
});
test('known verification/error gate is negative even when media exists',()=>{
  const {doc}=fixture({gate:true});assert.equal(sample(doc,url,'abcdefghijk',null,0).blockingGate,'TRUE');
});
test('unknown DOM and wrong video stay unknown',()=>{
  assert.equal(sample(fixture({layout:false}).doc,url,'abcdefghijk',null,0).blockingGate,'UNKNOWN');
  assert.equal(sample(fixture({id:'other-video'}).doc,url,'abcdefghijk',null,0).targetMatched,'UNKNOWN');
});
test('ready state does not establish media progress',()=>{
  const {doc}=fixture({paused:true});const s=sample(doc,url,'abcdefghijk',null,0);
  assert.equal(s.mediaProgress,'UNKNOWN');assert.equal(s.needsInteraction,true);
});
test('natural playback needs three seconds; seek jumps do not count',()=>{
  const {doc,video}=fixture();let s=sample(doc,url,'abcdefghijk',null,0);
  for(let i=1;i<=3;i++){video.currentTime=i;s=sample(doc,url,'abcdefghijk',s,i*1000);}
  assert.equal(s.mediaProgress,'TRUE');
  video.currentTime=70;s=sample(doc,url,'abcdefghijk',s,4000);assert.equal(s.mediaProgress,'UNKNOWN');
});
test('advertisements never establish selected content',()=>{
  const s=sample(fixture({ad:true}).doc,url,'abcdefghijk',null,0);
  assert.equal(s.contentPresent,'UNKNOWN');assert.equal(s.advertisement,true);
});
