// Packaged, isolated-world detector. Only structural booleans/numbers leave the document.
// Deliberately limited to YouTube watch pages; unknown layouts stay UNKNOWN.
(() => {
  if (globalThis.__dpiSocialDetector) return;
  function sample(document, location, expectedId, prior, now) {
    const unknown = {targetMatched:'UNKNOWN',contentPresent:'UNKNOWN',blockingGate:'UNKNOWN',mediaProgress:'UNKNOWN',
      documentVisible:document.visibilityState==='visible'?'TRUE':'FALSE',mediaProgressSeconds:0,seeking:false,advertisement:false,
      preview:false,needsInteraction:false,observedCapabilities:[],position:0,sampledAt:now};
    const url = new URL(location.href);
    if (!['www.youtube.com','m.youtube.com','youtube.com'].includes(url.hostname) || url.pathname!='/watch') return unknown;
    const watch = document.querySelector('ytd-watch-flexy[video-id]');
    if (url.searchParams.get('v')!==expectedId || !watch || watch.getAttribute('video-id')!==expectedId) return unknown;
    unknown.targetMatched='TRUE';
    const player=document.querySelector('#movie_player');
    const video=player?.querySelector('video.html5-main-video');
    if (!player || !video) return unknown;
    unknown.advertisement=player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting');
    unknown.preview=!!document.querySelector('ytd-video-preview[active]');
    const error = document.querySelector('ytd-player-error-message-renderer, ytd-enforcement-message-view-model, #player-error-message-container');
    const visibleError=error && error.getClientRects().length>0;
    const signIn=document.querySelector('ytd-player-legacy-desktop-watch-ads-renderer #sign-in-button, #player-error-message-container a[href*="accounts.google.com"]');
    unknown.blockingGate=visibleError || signIn ? 'TRUE' : (watch.hasAttribute('video-id') && player.classList.contains('html5-video-player') ? 'FALSE' : 'UNKNOWN');
    unknown.contentPresent=video.readyState>=2 && video.videoWidth>0 && !unknown.advertisement && !unknown.preview ? 'TRUE':'UNKNOWN';
    if (unknown.contentPresent==='TRUE') unknown.observedCapabilities.push('VIEW_SELECTED_CONTENT');
    unknown.seeking=!!video.seeking;
    unknown.position=Number.isFinite(video.currentTime)?video.currentTime:0;
    unknown.needsInteraction=video.paused && unknown.contentPresent==='TRUE' && !video.ended;
    if (prior && !video.paused && !video.seeking && !prior.seeking && !unknown.advertisement && !unknown.preview) {
      const delta=unknown.position-prior.position;
      const elapsed=(now-prior.sampledAt)/1000;
      if (delta>0 && elapsed>0 && delta<=elapsed*Math.max(video.playbackRate,1)+0.5)
        unknown.mediaProgressSeconds=(prior.mediaProgressSeconds||0)+delta;
    }
    if (unknown.mediaProgressSeconds>=3) { unknown.mediaProgress='TRUE';unknown.observedCapabilities.push('PLAY_MEDIA'); }
    return unknown;
  }
  globalThis.__dpiSocialDetector={sample};
  if (typeof module!=='undefined') module.exports={sample};
})();
