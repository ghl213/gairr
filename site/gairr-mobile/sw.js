const CACHE_NAME = 'gairr-v16';   // v16: 详情页左上角新增返回按钮，作废旧缓存   // v15: 撤销 v14 的底层强制色（恢复默认 #1a1a2e / light #f5f5f5），作废旧缓存
const urlsToCache = [
  '/',
  '/index.html',
  '/manifest.json',
  '/icon-192.png'
];

self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then(cache => cache.addAll(urlsToCache))
  );
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', event => {
  const req = event.request;
  // navigate: network-first (fresh page when online, cache fallback offline)
  if (req.mode === 'navigate') {
    event.respondWith(
      fetch(req)
        .then(res => {
          const copy = res.clone();
          caches.open(CACHE_NAME).then(c => c.put('/index.html', copy));
          return res;
        })
        .catch(() => caches.match('/index.html'))
    );
    return;
  }
  // static assets: cache-first
  event.respondWith(
    caches.match(req)
      .then(r => r || fetch(req))
  );
});
