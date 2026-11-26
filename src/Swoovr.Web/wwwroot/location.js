// One device location reading for an explicit check (D149 confirm-arrival, D150 home check, D152: the browser's location
// permission on the web). Asked for only when the member taps the button; never watched or tracked. The API decides what
// the reading means and keeps only the outcome. Resolves to null when the member declines or the browser can't tell.
window.swoovrLocation = {
  read: () => new Promise(resolve => {
    if (!navigator.geolocation) { resolve(null); return; }
    navigator.geolocation.getCurrentPosition(
      p => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude, accuracyMetres: p.coords.accuracy, takenAt: new Date(p.timestamp).toISOString() }),
      () => resolve(null),
      { enableHighAccuracy: true, timeout: 20000, maximumAge: 0 });
  }),
  // Issue 282: the Permissions API only chooses the wording ("granted", "prompt", "denied" or "unsupported"); it never prompts.
  permission: async () => {
    if (!navigator.geolocation) return 'unsupported';
    try { if (navigator.permissions?.query) return (await navigator.permissions.query({ name: 'geolocation' })).state; } catch { }
    return 'prompt';
  },
  // Issue 282: one Near me reading when the member opens or refreshes Near me. Status: ok, inaccurate (over 2 km), denied,
  // unsupported or unavailable. Never watched; the page asks again at most every 5 minutes while it is open and visible.
  readNearMe: () => new Promise(resolve => {
    if (!navigator.geolocation) { resolve({ status: 'unsupported', reading: null }); return; }
    navigator.geolocation.getCurrentPosition(
      p => {
        const reading = { latitude: p.coords.latitude, longitude: p.coords.longitude, accuracyMetres: p.coords.accuracy, takenAt: new Date(p.timestamp).toISOString() };
        resolve({ status: p.coords.accuracy > 2000 ? 'inaccurate' : 'ok', reading });
      },
      e => resolve({ status: e.code === 1 ? 'denied' : 'unavailable', reading: null }),
      { enableHighAccuracy: true, timeout: 20000, maximumAge: 0 });
  }),
  visible: () => document.visibilityState === 'visible',
};
