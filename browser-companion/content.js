(() => {
  if (window!==window.top || globalThis.__dpiSocialListening) return;
  globalThis.__dpiSocialListening=true;
  let timer=null, leaseTimer=null, stopped=false, generation=0, owned=false;
  const stop=(pause)=>{
    stopped=true;generation++;clearTimeout(timer);clearTimeout(leaseTimer);
    if(pause && owned) for(const video of document.querySelectorAll('video')) video.pause();
  };
  chrome.runtime.onMessage.addListener((message) => {
    if (message.kind==='stop') { stop(message.pauseOwned===true);return; }
    if (message.kind!=='observe') return;
    stop(false);stopped=false;owned=message.ownedRenderer===true;const run=generation;
    const binding=message.binding, start=performance.now(), original=location.href;
    const leaseMs=Math.min(20000,Math.max(0,Number.isFinite(message.leaseMs)?message.leaseMs:20000));
    leaseTimer=setTimeout(()=>stop(true),leaseMs);
    let prior=null, stableStart=0, stableSamples=0, stableSignature='', identityLost=false;
    const tick=async()=>{
      if (stopped || run!==generation) return;
      if (performance.now()-start>=leaseMs) {stop(true);return;}
      const now=performance.now();
      const signal=globalThis.__dpiSocialDetector.sample(document,location,new URL(binding.targetUrl).searchParams.get('v'),prior,now);
      if (location.href!==original || document.visibilityState!=='visible') { signal.targetMatched='UNKNOWN';signal.blockingGate='UNKNOWN'; }
      // Any observed login/logout surface invalidates the user's earlier session binding.
      const sessionChanged=!!document.querySelector('ytd-popup-container ytd-account-section-list-renderer, form[action*="ServiceLogin"]');
      identityLost=identityLost||sessionChanged;
      const sig=JSON.stringify([signal.targetMatched,signal.contentPresent,signal.blockingGate,signal.documentVisible,signal.advertisement,signal.preview]);
      if (sig!==stableSignature) { stableSignature=sig;stableStart=now;stableSamples=0; }
      stableSamples++; prior=signal;
      const {position,sampledAt,...safe}=signal;
      safe.cacheUncertain=!!navigator.serviceWorker?.controller;
      const response=await chrome.runtime.sendMessage({kind:'sample',documentToken:message.documentToken,signal:{...safe,
        observedAtMonotonic:now,observationWindow:now-start,stableSamples,stableDurationMs:now-stableStart,
        identityEvidence:binding.userBoundSession && !identityLost ? 'USER_BOUND_SESSION':'UNKNOWN'}}).catch(()=>null);
      if (!response?.ok || stopped || run!==generation) return;
      timer=setTimeout(tick,1000);
    };
    tick();
  });
  addEventListener('pagehide',()=>stop(false),{once:true});
})();
