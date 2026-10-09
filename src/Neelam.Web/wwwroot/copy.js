// Copy buttons for the export: a button with data-copy="<id>" copies that element's text. One
// listener for the whole page, so it works however the page was rendered, with no interop. The
// button is marked with data-copied for a moment; if the browser refuses the clipboard, the text is
// selected instead, ready for Ctrl+C.
document.addEventListener("click", async (event) => {
    const button = event.target instanceof Element ? event.target.closest("[data-copy]") : null;
    if (!button) return;
    const source = document.getElementById(button.getAttribute("data-copy"));
    if (!source) return;
    try {
        await navigator.clipboard.writeText(source.textContent);
        button.setAttribute("data-copied", "");
        setTimeout(() => button.removeAttribute("data-copied"), 2000);
    } catch {
        window.getSelection()?.selectAllChildren(source);
    }
});
