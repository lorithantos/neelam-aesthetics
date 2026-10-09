// Copy buttons for the export: a button with data-copy="<id>" copies that element's text. One
// listener for the whole page, so it works however the page was rendered, with no interop. The
// button is marked with data-copied for a moment; if the browser refuses the clipboard, the text is
// selected instead and a line beside the button says to copy it by hand, so a refusal never passes
// for a copy. Wrapped so nothing is declared globally.
(() => {
    const copyByHand = "Press Ctrl+C (or Cmd+C) to copy";

    // The line that says what to do when the clipboard is refused: the page's own "<id>-hint"
    // element when it has one (so nothing is added to markup the page renders), else one made beside
    // the button once. Either is announced when it changes.
    const hintFor = (button) => {
        const own = document.getElementById(button.getAttribute("data-copy") + "-hint");
        if (own) return own;
        const next = button.nextElementSibling;
        if (next && next.hasAttribute("data-copy-hint")) return next;
        const hint = document.createElement("span");
        hint.setAttribute("data-copy-hint", "");
        hint.setAttribute("role", "status");
        hint.className = "field-help";
        button.insertAdjacentElement("afterend", hint);
        return hint;
    };

    document.addEventListener("click", async (event) => {
        const button = event.target instanceof Element ? event.target.closest("[data-copy]") : null;
        if (!button) return;
        const source = document.getElementById(button.getAttribute("data-copy"));
        if (!source) return;
        const hint = hintFor(button);
        try {
            await navigator.clipboard.writeText(source.textContent);
            hint.textContent = "";
            button.setAttribute("data-copied", "");
            setTimeout(() => button.removeAttribute("data-copied"), 2000);
        } catch {
            window.getSelection()?.selectAllChildren(source);
            hint.textContent = copyByHand;
        }
    });
})();
