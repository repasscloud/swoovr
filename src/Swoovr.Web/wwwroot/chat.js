// Issue 542: the conversation's own scrolling thread. Blazor calls these through SwoovrChat; nothing runs on other pages.
(function () {
  'use strict';
  const nearEnd = el => el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  window.SwoovrChat = {
    // Opening a chat: the first unread message near the top when there is one, else the newest message.
    start(el, firstUnreadId) {
      if (!el) return;
      const target = firstUnreadId ? el.querySelector(`[data-message="${CSS.escape(firstUnreadId)}"]`) : null;
      if (target) el.scrollTop = Math.max(0, target.offsetTop - el.offsetTop - 16);
      else el.scrollTop = el.scrollHeight;
    },
    // Before older messages are added above: remember the height, so the member stays where they were reading.
    height(el) { return el ? el.scrollHeight : 0; },
    keep(el, previousHeight) { if (el) el.scrollTop += el.scrollHeight - previousHeight; },
    // A new message: follow it only when the member was already at the end (or sent it), never pull them down mid-read.
    atEnd(el) { return !el || nearEnd(el); },
    toEnd(el) { if (el) el.scrollTop = el.scrollHeight; },
  };
})();
