document.getElementById('bind').addEventListener('click', async () => {
  const [tab] = await chrome.tabs.query({active:true,currentWindow:true});
  const result = await chrome.runtime.sendMessage({kind:'bind',tabId:tab.id,sameAccount:document.getElementById('account').checked,
    navigate:document.getElementById('navigate').checked});
  document.getElementById('status').textContent = result?.ok ? 'Gözlem bağlı. Uygulamada denemeyi başlatabilirsiniz. Test sekmesini görünür tutun.' : 'Uygulama/yerel gözlemci bağlantısı yok veya hedef desteklenmiyor.';
});
document.getElementById('stop').addEventListener('click', async () => {
  await chrome.runtime.sendMessage({kind:'stop'}); document.getElementById('status').textContent='Gözlem durduruldu.';
});
