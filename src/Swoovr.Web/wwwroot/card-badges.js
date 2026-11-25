// PR 501 review (D406): Escape dismisses a badge explanation opened by hovering, when focus is elsewhere and the button gets
// no key events. Delegated so it survives Blazor enhanced navigation; hovering again reopens it.
(() => {
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    const badges = document.querySelector('.mc-badges:hover'); if (!badges) return;
    badges.dataset.hoverDismissed = '';
    badges.addEventListener('pointerleave', () => delete badges.dataset.hoverDismissed, { once: true });
  });
})();
