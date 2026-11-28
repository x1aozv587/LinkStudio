// 工作台轻量交互脚本：智能自动滚屏、接收区局部 Ctrl+A、剪贴板复制、多行发送快捷键与设置页滚动联动。

export function scrollToBottom(element, force = false) {
    if (!element) {
        return;
    }

    // 用户已经主动往上翻阅时，除非明确点击“回到底部”(force=true)，否则绝不抢拉滚动条。
    if (!force && element.dataset.userScrolledUp === "true") {
        return;
    }

    if (force) {
        // 给予 250ms 保护窗口，避免高速数据在点击“回到底部”的瞬间撑高内容导致误判为上翻。
        element.dataset.forceBottomUntil = String(Date.now() + 250);
    }

    element.dataset.userScrolledUp = "false";
    element.scrollTop = element.scrollHeight;
    element.dataset.lastScrollTop = String(element.scrollTop);
}

export function scrollToEntry(container, entryId) {
    if (!container || !entryId) {
        return;
    }

    const target = container.querySelector(`[data-entry-id="${entryId}"]`);
    if (target) {
        target.scrollIntoView({ behavior: "smooth", block: "center" });
    }
}

export function attachAutoScrollWatcher(element, dotNetRef) {
    if (!element || !dotNetRef) {
        return null;
    }

    let lastNearBottom = true;
    let userScrollUpIntentUntil = 0;

    // 只有真实的用户向上交互（滚轮上滑、按住滚动条、方向/翻页键）才允许锁定停止自动滚底，
    // 彻底杜绝高速日志达到行数上限从顶部裁剪 DOM 时，浏览器自动钳位 scrollTop 造成的误停。
    const markUserScrollUpIntent = () => {
        userScrollUpIntentUntil = Date.now() + 400;
    };

    const onWheel = (e) => {
        if (e.deltaY < 0) {
            markUserScrollUpIntent();
        }
    };

    const onPointerDown = () => {
        markUserScrollUpIntent();
    };

    const onScroll = () => {
        const forceUntil = Number(element.dataset.forceBottomUntil || "0");
        if (Date.now() < forceUntil) {
            element.scrollTop = element.scrollHeight;
            element.dataset.userScrolledUp = "false";
            if (!lastNearBottom) {
                lastNearBottom = true;
                dotNetRef.invokeMethodAsync("OnTerminalScrollPositionChanged", true);
            }
            return;
        }

        // 距离底部 28px 以内视为贴底。
        const distanceToBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
        let isNearBottom = distanceToBottom <= 28;

        // 如果当前原本在贴底跟随状态，且没有真实的用户上滚意图，则维持贴底，绝不因 DOM 裁剪自动停住。
        if (!isNearBottom && lastNearBottom && Date.now() > userScrollUpIntentUntil) {
            isNearBottom = true;
        }

        element.dataset.userScrolledUp = isNearBottom ? "false" : "true";
        if (isNearBottom !== lastNearBottom) {
            lastNearBottom = isNearBottom;
            dotNetRef.invokeMethodAsync("OnTerminalScrollPositionChanged", isNearBottom);
        }
    };

    const onKeyDown = (e) => {
        if (e.key === "PageUp" || e.key === "ArrowUp" || e.key === "Home") {
            markUserScrollUpIntent();
        }

        // 在接收区按 Ctrl+A / Cmd+A 时，仅全选接收区内部的日志文本，避免选中整个页面按钮。
        if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === "a") {
            e.preventDefault();
            const selection = window.getSelection();
            if (!selection) {
                return;
            }
            const range = document.createRange();
            range.selectNodeContents(element);
            selection.removeAllRanges();
            selection.addRange(range);
        }
    };

    const onMouseDown = (e) => {
        // 浏览器会在双击的第 2 次 mousedown 就立刻画出蓝色选区；
        // 仅在 e.detail >= 2 时阻止默认选词，既彻底消除蓝块闪烁，又不影响正常的单击拖拽选中。
        if (e.detail >= 2) {
            e.preventDefault();
        }
    };

    element.addEventListener("scroll", onScroll, { passive: true });
    element.addEventListener("wheel", onWheel, { passive: true });
    element.addEventListener("pointerdown", onPointerDown, { passive: true });
    element.addEventListener("keydown", onKeyDown);
    element.addEventListener("mousedown", onMouseDown);

    return {
        dispose() {
            element.removeEventListener("scroll", onScroll);
            element.removeEventListener("wheel", onWheel);
            element.removeEventListener("pointerdown", onPointerDown);
            element.removeEventListener("keydown", onKeyDown);
            element.removeEventListener("mousedown", onMouseDown);
        }
    };
}

export async function copyTextToClipboard(text, clearSelection = false) {
    if (!text) {
        return false;
    }

    if (clearSelection) {
        // 清除浏览器双击文本时默认产生的单词/空白蓝色选区。
        window.getSelection()?.removeAllRanges();
    }

    try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        // 部分 WebView 场景下回退到 execCommand 复制。
    }

    const helper = document.createElement("textarea");
    helper.value = text;
    helper.style.position = "fixed";
    helper.style.opacity = "0";
    document.body.appendChild(helper);
    helper.select();
    const ok = document.execCommand("copy");
    document.body.removeChild(helper);
    if (clearSelection) {
        window.getSelection()?.removeAllRanges();
    }
    return ok;
}

export function attachSendTextareaKeyHandler(textarea, dotNetRef) {
    if (!textarea || !dotNetRef) {
        return null;
    }

    const onKeyDown = (e) => {
        if (e.isComposing || e.key !== "Enter") {
            return;
        }

        const enterToSend = textarea.dataset.enterToSend === "true";
        const shouldSend = enterToSend
            ? (!e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey)
            : (e.ctrlKey || e.metaKey);

        if (shouldSend) {
            // 仅在真正触发发送时阻止换行，确保 Shift+Enter 或普通换行完全顺滑。
            e.preventDefault();
            dotNetRef.invokeMethodAsync("OnSendShortcutAsync");
        }
    };

    textarea.addEventListener("keydown", onKeyDown);

    return {
        dispose() {
            textarea.removeEventListener("keydown", onKeyDown);
        }
    };
}

export function attachSettingsScrollSpy(container, dotNetRef) {
    if (!container || !dotNetRef) {
        return null;
    }

    let currentId = null;

    const updateActiveSection = () => {
        const sections = Array.from(container.querySelectorAll("[data-settings-section]"));
        if (sections.length === 0) {
            return;
        }

        const containerRect = container.getBoundingClientRect();
        const atBottom = container.scrollHeight - container.scrollTop - container.clientHeight <= 16;

        let nextId = sections[0].getAttribute("data-settings-section");
        if (atBottom) {
            nextId = sections[sections.length - 1].getAttribute("data-settings-section");
        } else {
            const anchorY = containerRect.top + 88;
            for (const section of sections) {
                const rect = section.getBoundingClientRect();
                if (rect.top <= anchorY) {
                    nextId = section.getAttribute("data-settings-section");
                }
            }
        }

        if (nextId && nextId !== currentId) {
            currentId = nextId;
            dotNetRef.invokeMethodAsync("OnActiveSectionChanged", nextId);
        }
    };

    container.addEventListener("scroll", updateActiveSection, { passive: true });

    return {
        resetScroll() {
            container.scrollTop = 0;
            currentId = null;
        },
        dispose() {
            container.removeEventListener("scroll", updateActiveSection);
        }
    };
}

export function scrollToSection(container, sectionId) {
    if (!container || !sectionId) {
        return;
    }

    const target = container.querySelector(`[data-settings-section="${sectionId}"]`);
    if (target) {
        target.scrollIntoView({ behavior: "smooth", block: "start" });
    }
}

export function attachVerticalSplitter(handle, panel) {
    if (!handle || !panel) {
        return null;
    }

    let dragging = false;
    let startY = 0;
    let startHeight = 0;

    const onPointerDown = (e) => {
        dragging = true;
        startY = e.clientY;
        startHeight = panel.getBoundingClientRect().height;
        handle.setPointerCapture(e.pointerId);
        document.body.style.userSelect = "none";
        e.preventDefault();
    };

    const onPointerMove = (e) => {
        if (!dragging) {
            return;
        }

        // 向上拖动增加过滤面板高度，向下拖动缩小，限制在合理区间内。
        const delta = startY - e.clientY;
        const maxH = Math.max(200, Math.floor(window.innerHeight * 0.58));
        const nextHeight = Math.min(maxH, Math.max(130, Math.round(startHeight + delta)));
        panel.style.height = `${nextHeight}px`;
    };

    const stopDrag = (e) => {
        if (!dragging) {
            return;
        }

        dragging = false;
        if (handle.hasPointerCapture(e.pointerId)) {
            handle.releasePointerCapture(e.pointerId);
        }
        document.body.style.userSelect = "";
    };

    handle.addEventListener("pointerdown", onPointerDown);
    handle.addEventListener("pointermove", onPointerMove);
    handle.addEventListener("pointerup", stopDrag);
    handle.addEventListener("pointercancel", stopDrag);

    return {
        dispose() {
            handle.removeEventListener("pointerdown", onPointerDown);
            handle.removeEventListener("pointermove", onPointerMove);
            handle.removeEventListener("pointerup", stopDrag);
            handle.removeEventListener("pointercancel", stopDrag);
        }
    };
}

export function attachWaveRangeNavigator(track, dotNetRef) {
    if (!track || !dotNetRef) {
        return null;
    }

    const MIN_SPAN = 0.05;
    let mode = null;
    let originX = 0;
    let originStart = 0;
    let originEnd = 1;
    let pendingRange = null;
    let rafId = 0;

    const clamp = (v, min, max) => Math.min(max, Math.max(min, v));

    const flush = () => {
        rafId = 0;
        if (!pendingRange) {
            return;
        }
        const [s, e] = pendingRange;
        pendingRange = null;
        dotNetRef.invokeMethodAsync("OnRangeChanged", s, e);
    };

    const emit = (s, e) => {
        pendingRange = [s, e];
        if (!rafId) {
            rafId = requestAnimationFrame(flush);
        }
    };

    const onPointerDown = (e) => {
        if (e.button !== 0) {
            return;
        }

        const rect = track.getBoundingClientRect();
        if (rect.width <= 0) {
            return;
        }

        originStart = parseFloat(track.dataset.start || "0");
        originEnd = parseFloat(track.dataset.end || "1");
        originX = e.clientX;

        const targetMode = e.target?.closest?.("[data-drag-mode]")?.getAttribute("data-drag-mode");
        if (targetMode === "left" || targetMode === "right" || targetMode === "move") {
            mode = targetMode;
        } else {
            // 点击空白缩略图区域时，把当前窗口中心快速移动到点击处，并允许顺势拖动。
            const clickRatio = clamp((e.clientX - rect.left) / rect.width, 0, 1);
            const span = Math.max(MIN_SPAN, originEnd - originStart);
            const nextStart = clamp(clickRatio - span / 2, 0, 1 - span);
            originStart = nextStart;
            originEnd = nextStart + span;
            mode = "move";
            emit(originStart, originEnd);
        }

        track.setPointerCapture(e.pointerId);
        document.body.style.userSelect = "none";
        e.preventDefault();
    };

    const onPointerMove = (e) => {
        if (!mode) {
            return;
        }

        const rect = track.getBoundingClientRect();
        if (rect.width <= 0) {
            return;
        }

        const dx = (e.clientX - originX) / rect.width;
        let nextStart = originStart;
        let nextEnd = originEnd;

        if (mode === "move") {
            const span = originEnd - originStart;
            nextStart = clamp(originStart + dx, 0, 1 - span);
            nextEnd = nextStart + span;
        } else if (mode === "left") {
            nextStart = clamp(originStart + dx, 0, originEnd - MIN_SPAN);
        } else if (mode === "right") {
            nextEnd = clamp(originEnd + dx, originStart + MIN_SPAN, 1);
        }

        emit(nextStart, nextEnd);
    };

    const stopDrag = (e) => {
        if (!mode) {
            return;
        }

        mode = null;
        if (track.hasPointerCapture(e.pointerId)) {
            track.releasePointerCapture(e.pointerId);
        }
        document.body.style.userSelect = "";
    };

    track.addEventListener("pointerdown", onPointerDown);
    track.addEventListener("pointermove", onPointerMove);
    track.addEventListener("pointerup", stopDrag);
    track.addEventListener("pointercancel", stopDrag);

    return {
        dispose() {
            if (rafId) {
                cancelAnimationFrame(rafId);
                rafId = 0;
            }
            track.removeEventListener("pointerdown", onPointerDown);
            track.removeEventListener("pointermove", onPointerMove);
            track.removeEventListener("pointerup", stopDrag);
            track.removeEventListener("pointercancel", stopDrag);
        }
    };
}

export function attachWaveHoverTracker(svg, dotNetRef) {
    if (!svg || !dotNetRef) {
        return null;
    }

    let rafId = 0;
    let pendingPoint = null;

    const flush = () => {
        rafId = 0;
        if (!pendingPoint) {
            return;
        }
        const pt = pendingPoint;
        pendingPoint = null;
        if (pt.leave) {
            dotNetRef.invokeMethodAsync("OnSvgHoverLeave");
        } else {
            dotNetRef.invokeMethodAsync("OnSvgHoverMove", pt.x, pt.y);
        }
    };

    const onPointerMove = (e) => {
        const ctm = svg.getScreenCTM();
        if (!ctm) {
            return;
        }

        // 用 SVG 逆矩阵把屏幕像素精确映射到 viewBox 坐标，任意窗口缩放比例下都零误差。
        const pt = svg.createSVGPoint();
        pt.x = e.clientX;
        pt.y = e.clientY;
        const local = pt.matrixTransform(ctm.inverse());
        pendingPoint = { leave: false, x: local.x, y: local.y };
        if (!rafId) {
            rafId = requestAnimationFrame(flush);
        }
    };

    const onPointerLeave = () => {
        pendingPoint = { leave: true };
        if (!rafId) {
            rafId = requestAnimationFrame(flush);
        }
    };

    const onWheel = (e) => {
        const ctm = svg.getScreenCTM();
        if (!ctm) {
            return;
        }

        const pt = svg.createSVGPoint();
        pt.x = e.clientX;
        pt.y = e.clientY;
        const local = pt.matrixTransform(ctm.inverse());
        // 仅在绘图区范围内拦截滚轮用于波形缩放，避免影响其他区域滚动。
        if (local.x >= 50 && local.x <= 872 && local.y >= 8 && local.y <= 258) {
            e.preventDefault();
            const ratio = Math.min(1, Math.max(0, (local.x - 58) / (864 - 58)));
            dotNetRef.invokeMethodAsync("OnSvgWheelZoom", ratio, e.deltaY);
        }
    };

    svg.addEventListener("pointermove", onPointerMove);
    svg.addEventListener("pointerleave", onPointerLeave);
    svg.addEventListener("wheel", onWheel, { passive: false });

    return {
        dispose() {
            if (rafId) {
                cancelAnimationFrame(rafId);
                rafId = 0;
            }
            svg.removeEventListener("pointermove", onPointerMove);
            svg.removeEventListener("pointerleave", onPointerLeave);
            svg.removeEventListener("wheel", onWheel);
        }
    };
}

export function attachCanvasDragReorder(grid, dotNetRef) {
    if (!grid || !dotNetRef) {
        return null;
    }

    let sourceCell = null;
    let sourceId = null;
    let targetCell = null;
    let targetId = null;
    let ghostEl = null;

    const clearTarget = () => {
        if (targetCell) {
            targetCell.classList.remove("is-drag-over");
            targetCell = null;
            targetId = null;
        }
    };

    const removeGhost = () => {
        if (ghostEl) {
            ghostEl.remove();
            ghostEl = null;
        }
    };

    const positionGhost = (x, y) => {
        if (!ghostEl) {
            return;
        }
        ghostEl.style.transform = `translate3d(${Math.round(x + 14)}px, ${Math.round(y + 12)}px, 0) rotate(0.8deg)`;
    };

    const onPointerDown = (e) => {
        if (e.button !== 0) {
            return;
        }

        // 仅在按住拖动把手（或工具栏空白处，排除输入框与按钮）时启动组件拖拽。
        const handle = e.target?.closest?.("[data-drag-handle]");
        if (!handle || e.target?.closest?.("input, button")) {
            return;
        }

        const cell = handle.closest("[data-widget-id]");
        if (!cell) {
            return;
        }

        sourceCell = cell;
        sourceId = cell.dataset.widgetId;
        sourceCell.classList.add("is-dragging");

        // 创建跟随鼠标的悬浮预览卡片，让拖拽过程有清晰直观的视觉反馈。
        const titleInput = cell.querySelector(".ls-cell-title-input");
        const label = (titleInput && titleInput.value.trim()) || "移动组件";
        ghostEl = document.createElement("div");
        ghostEl.className = "ls-drag-ghost";
        ghostEl.textContent = `⋮⋮  ${label}`;
        document.body.appendChild(ghostEl);
        positionGhost(e.clientX, e.clientY);

        grid.setPointerCapture(e.pointerId);
        document.body.style.userSelect = "none";
        e.preventDefault();
    };

    const onPointerMove = (e) => {
        if (!sourceCell) {
            return;
        }

        positionGhost(e.clientX, e.clientY);

        const hit = document.elementsFromPoint(e.clientX, e.clientY)
            .map((el) => el.closest?.("[data-widget-id]"))
            .find((el) => el && el !== sourceCell && grid.contains(el));

        if (hit !== targetCell) {
            clearTarget();
            if (hit) {
                targetCell = hit;
                targetId = hit.dataset.widgetId;
                targetCell.classList.add("is-drag-over");
            }
        }
    };

    const finishDrag = (e) => {
        if (!sourceCell) {
            return;
        }

        const fromId = sourceId;
        const toId = targetId;

        sourceCell.classList.remove("is-dragging");
        sourceCell = null;
        sourceId = null;
        clearTarget();
        removeGhost();

        if (grid.hasPointerCapture(e.pointerId)) {
            grid.releasePointerCapture(e.pointerId);
        }
        document.body.style.userSelect = "";

        if (fromId && toId && fromId !== toId) {
            dotNetRef.invokeMethodAsync("OnWidgetReordered", fromId, toId);
        }
    };

    grid.addEventListener("pointerdown", onPointerDown);
    grid.addEventListener("pointermove", onPointerMove);
    grid.addEventListener("pointerup", finishDrag);
    grid.addEventListener("pointercancel", finishDrag);

    return {
        dispose() {
            removeGhost();
            grid.removeEventListener("pointerdown", onPointerDown);
            grid.removeEventListener("pointermove", onPointerMove);
            grid.removeEventListener("pointerup", finishDrag);
            grid.removeEventListener("pointercancel", finishDrag);
        }
    };
}
