// Arriving at one of her benefit lines from a finding ("Change this line's range", to
// known-items?cap=highest-percent#known-line-<id>): the line is scrolled to the middle of the page,
// marked with data-arrived for a moment, and focus put in the box ?cap names (its data-cap: the limit
// the amount fell outside), or its first box without one, so its range can be typed straight away.
// As copy.js does, one script for every page, no interop, nothing declared globally.
//
// The page is sent first and turns interactive after, which can replace the line's element, so the
// line is looked for again whenever the page changes, for a few seconds after arriving; each
// element is handled once. Arriving is a page load, Blazor's enhanced navigation, or a new #hash.
(() => {
    const prefix = "known-line-";
    const watchFor = 10000;
    const markFor = 2500;
    let handled = null;
    let until = 0;

    const reveal = () => {
        const id = decodeURIComponent(location.hash.slice(1));
        if (!id.startsWith(prefix)) return;
        const line = document.getElementById(id);
        if (!line || line === handled) return;
        handled = line;
        line.scrollIntoView({ block: "center" });
        line.setAttribute("data-arrived", "");
        setTimeout(() => line.removeAttribute("data-arrived"), markFor);
        const cap = new URLSearchParams(location.search).get("cap");
        const box = cap && line.querySelector(`input[data-cap="${CSS.escape(cap)}"]`);
        (box || line.querySelector("input") || line).focus({ preventScroll: true });
    };

    const arrive = () => {
        handled = null;
        until = Date.now() + watchFor;
        reveal();
    };

    new MutationObserver(() => {
        if (Date.now() < until) reveal();
    }).observe(document.body, { childList: true, subtree: true });
    window.addEventListener("hashchange", arrive);
    window.Blazor?.addEventListener?.("enhancedload", arrive);
    arrive();
})();
