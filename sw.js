const CACHE='lofi-room-v33';
const ASSET_VERSION='20260810d';
const v=path=>path.includes('?')?path:path+'?v='+ASSET_VERSION;
const ASSETS=['./','index.html','manifest.webmanifest','presets.json','assets/app-icon.png','assets/chilling.png','assets/busy.png','assets/away.png','assets/ems.jpg','assets/training.jpg','assets/gaming.jpg'].map(v);
self.addEventListener('install',event=>{event.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(key=>key.startsWith('lofi-room-')&&key!==CACHE).map(key=>caches.delete(key)))).then(()=>caches.open(CACHE)).then(cache=>cache.addAll(ASSETS)));self.skipWaiting();});
self.addEventListener('activate',event=>{event.waitUntil(caches.keys().then(keys=>Promise.all(keys.filter(key=>key!==CACHE).map(key=>caches.delete(key)))));self.clients.claim();});
self.addEventListener('fetch',event=>{if(event.request.method!=='GET')return;event.respondWith(fetch(event.request).then(response=>{const copy=response.clone();caches.open(CACHE).then(cache=>cache.put(event.request,copy));return response;}).catch(()=>caches.match(event.request).then(cached=>cached||caches.match('index.html'))));});










