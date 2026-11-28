// 工作台轻量交互脚本：智能自动滚屏、接收区局部 Ctrl+A、剪贴板复制、多行发送快捷键与设置页滚动联动。

export function scrollToBottom(element) {
    if (!element) {
        return;
    }
    element.scrollTop = element.scrollHeight;
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

    const onScroll = () => {
        // 距离底部 28px 以内视为贴底，避免亚像素舍入导致状态闪烁。
        const distanceToBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
        const isNearBottom = distanceToBottom <= 28;
        if (isNearBottom !== lastNearBottom) {
            lastNearBottom = isNearBottom;
            dotNetRef.invokeMethodAsync("OnTerminalScrollPositionChanged", isNearBottom);
        }
    };

    const onKeyDown = (e) => {
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
    element.addEventListener("keydown", onKeyDown);
    element.addEventListener("mousedown", onMouseDown);

    return {
        dispose() {
            element.removeEventListener("scroll", onScroll);
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
