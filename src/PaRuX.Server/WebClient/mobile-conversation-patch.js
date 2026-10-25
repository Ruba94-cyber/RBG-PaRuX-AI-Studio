(() => {
  "use strict";

  const ready = (fn) => {
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", fn, { once: true });
    else fn();
  };

  ready(() => {
    const chatPage = document.getElementById("chatpage");
    const topbar = chatPage?.querySelector(".topbar");
    const conversations = chatPage?.querySelector(".convlist");
    const newChat = document.getElementById("newchat");
    const conversationList = document.getElementById("convs");
    if (!chatPage || !topbar || !conversations || !newChat || !conversationList) return;
    if (document.getElementById("mobile-conversations-toggle")) return;

    conversations.id ||= "conversation-list";

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.id = "mobile-conversations-toggle";
    toggle.textContent = "Conversations";
    toggle.setAttribute("aria-controls", conversations.id);
    toggle.setAttribute("aria-expanded", "false");
    toggle.setAttribute("aria-label", "Open conversations");
    topbar.insertBefore(toggle, topbar.firstChild);

    const backdrop = document.createElement("button");
    backdrop.type = "button";
    backdrop.id = "mobile-conversations-backdrop";
    backdrop.setAttribute("aria-label", "Close conversations");
    backdrop.tabIndex = -1;
    document.body.appendChild(backdrop);

    const media = window.matchMedia("(max-width: 800px)");

    const setOpen = (open, restoreFocus = false) => {
      const effectiveOpen = Boolean(open && media.matches);
      document.body.classList.toggle("mobile-conversations-open", effectiveOpen);
      toggle.setAttribute("aria-expanded", String(effectiveOpen));
      toggle.setAttribute("aria-label", effectiveOpen ? "Close conversations" : "Open conversations");
      const restoreDrawerFocus = media.matches && !effectiveOpen
        && conversations.contains(document.activeElement);
      conversations.inert = media.matches && !effectiveOpen;
      conversations.setAttribute("aria-hidden", String(media.matches && !effectiveOpen));
      if (effectiveOpen) {
        requestAnimationFrame(() => newChat.focus());
      } else if (restoreFocus || restoreDrawerFocus) {
        requestAnimationFrame(() => toggle.focus());
      }
    };

    toggle.addEventListener("click", () => {
      setOpen(!document.body.classList.contains("mobile-conversations-open"), true);
    });
    backdrop.addEventListener("click", () => setOpen(false, true));

    document.addEventListener("keydown", (event) => {
      if (event.key !== "Escape" || !document.body.classList.contains("mobile-conversations-open")) return;
      event.preventDefault();
      setOpen(false, true);
    });

    newChat.addEventListener("click", () => setOpen(false));
    conversationList.addEventListener("click", (event) => {
      const target = event.target instanceof Element ? event.target.closest("button.open") : null;
      if (target) setOpen(false);
    });

    const syncViewport = () => setOpen(false);
    if (typeof media.addEventListener === "function") media.addEventListener("change", syncViewport);
    else if (typeof media.addListener === "function") media.addListener(syncViewport);
    syncViewport();
  });
})();
