// Arrange blocks on the template page (owner, 2026-11-03: "create a set of proxy blocks with the
// names that they can see on a single page that allows them to drag and drop").
//
// A chip is dragged on pointer events, so a mouse, a finger and a pen all work; HTML5 drag and drop
// does not work on touch screens. With a mouse or a pen any part of a chip but its buttons drags it;
// on a touch screen only its handle does, so the rest of the list still scrolls the page.
//
// Blazor draws the list, so nothing here moves, adds or removes an element of it. While dragging, the
// chip follows the pointer by a transform and a line, outside the list, shows where it will land. On
// drop both are put back and .NET is told MoveBlock(from, to) through the reference the page gave
// attach(); the form moves the block and Blazor draws the list again in its new order. Escape, or the
// browser taking the pointer away, cancels the drag and changes nothing.
//
// One name is declared globally, neelamArrange, for the page to call: attach(list, dotnet) once the
// list is drawn, and focus(list, index, which) after Up or Down, since a browser takes focus off an
// element that Blazor moves.
window.neelamArrange = (() => {
    // How far a press moves before it becomes a drag, so a press that does not move stays a press.
    const threshold = 4;
    let drag = null;

    const chipsOf = list => Array.from(list.children).filter(c => c.hasAttribute("data-arrange-chip"));

    // Where the chip would land: how many of the other chips have their middle above the pointer.
    const landing = (others, y) => others.filter(c => {
        const r = c.getBoundingClientRect();
        return r.top + r.height / 2 < y;
    }).length;

    const showLine = (d, others, to) => {
        const box = d.list.getBoundingClientRect();
        // Halfway between the chips it falls between, or just outside the first or the last.
        const above = to > 0 ? others[to - 1].getBoundingClientRect().bottom : null;
        const below = to < others.length ? others[to].getBoundingClientRect().top : null;
        const y = above !== null && below !== null ? (above + below) / 2
            : below !== null ? below - 2
            : above !== null ? above + 2
            : box.top;
        if (!d.line) {
            d.line = document.createElement("div");
            d.line.className = "arrange-line";
            d.line.setAttribute("aria-hidden", "true");
            document.body.appendChild(d.line);
        }
        d.line.style.left = `${box.left}px`;
        d.line.style.width = `${box.width}px`;
        d.line.style.top = `${y - 1.5}px`;
    };

    // Everything the drag changed put back, so the list is exactly as Blazor drew it.
    const finish = () => {
        const d = drag;
        if (!d) return null;
        drag = null;
        d.stop.abort();
        d.chip.style.transform = "";
        d.chip.removeAttribute("data-arrange-dragging");
        d.line?.remove();
        if (d.chip.hasPointerCapture?.(d.pointerId)) d.chip.releasePointerCapture(d.pointerId);
        return d;
    };

    const move = e => {
        const d = drag;
        if (!d || e.pointerId !== d.pointerId) return;
        const dy = e.clientY - d.startY;
        if (!d.started) {
            if (Math.abs(dy) < threshold && Math.abs(e.clientX - d.startX) < threshold) return;
            d.started = true;
            d.chip.setAttribute("data-arrange-dragging", "");
        }
        e.preventDefault();
        // The chip follows the pointer, allowing for the page scrolled since the press.
        d.chip.style.transform = `translateY(${dy + window.scrollY - d.startScroll}px)`;
        const others = chipsOf(d.list).filter(c => c !== d.chip);
        d.to = landing(others, e.clientY);
        showLine(d, others, d.to);
    };

    const drop = e => {
        if (!drag || e.pointerId !== drag.pointerId) return;
        const d = finish();
        if (!d.started || d.to === d.from) return;
        d.list.neelamArrange?.invokeMethodAsync("MoveBlock", d.from, d.to)
            .catch(err => console.warn("Arrange blocks: the move did not reach the page.", err));
    };

    const cancel = () => finish();

    const key = e => {
        if (e.key !== "Escape" || !drag) return;
        e.preventDefault();
        e.stopPropagation();
        finish();
    };

    const press = (list, e) => {
        if (drag || !e.isPrimary) return;
        if (e.pointerType === "mouse" && e.button !== 0) return;
        const chip = e.target.closest("[data-arrange-chip]");
        if (!chip || chip.parentElement !== list) return;
        if (e.target.closest("button, a, input, select, textarea")) return;
        if (e.pointerType === "touch" && !e.target.closest("[data-arrange-handle]")) return;

        const from = chipsOf(list).indexOf(chip);
        if (from < 0) return;
        e.preventDefault();
        const stop = new AbortController();
        drag = {
            list, chip, from, to: from, pointerId: e.pointerId, started: false, line: null, stop,
            startX: e.clientX, startY: e.clientY, startScroll: window.scrollY,
        };
        chip.setPointerCapture(e.pointerId);
        chip.addEventListener("pointermove", move, { signal: stop.signal });
        chip.addEventListener("pointerup", drop, { signal: stop.signal });
        chip.addEventListener("pointercancel", cancel, { signal: stop.signal });
        chip.addEventListener("lostpointercapture", cancel, { signal: stop.signal });
        document.addEventListener("keydown", key, { capture: true, signal: stop.signal });
    };

    return {
        attach(list, dotnet) {
            if (!list) return;
            // Properties of the element, not attributes, so nothing Blazor draws is changed.
            list.neelamArrange = dotnet;
            if (list.neelamArrangeAttached) return;
            list.neelamArrangeAttached = true;
            list.addEventListener("pointerdown", e => press(list, e));
        },

        // The moved chip's Up or Down, or its other one when that end has been reached.
        focus(list, index, which) {
            const chip = list && chipsOf(list)[index];
            if (!chip) return;
            const wanted = chip.querySelector(`[data-arrange-move="${which}"]`);
            const target = wanted && !wanted.disabled ? wanted : chip.querySelector("[data-arrange-move]:not([disabled])");
            target?.focus();
        },
    };
})();
