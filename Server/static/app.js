let peers = [];
let groups = [];
let me = {
    username: "—",
    is_admin: false,
    group: null
};

let selectedGroup = localStorage.getItem("vpnMonitor.selectedGroup") || null;
let selectedFilter = localStorage.getItem("vpnMonitor.selectedFilter") || "all";
let selectedPeer = null;
let selectedPeerId = null;
let searchText = "";

const allowedSortColumns = [
    "status",
    "name",
    "ip",
    "handshake",
    "traffic",
    "groups"
];

const savedSortColumn = localStorage.getItem("vpnMonitor.sortColumn");
const savedSortDirection = localStorage.getItem("vpnMonitor.sortDirection");

let sortColumn = allowedSortColumns.includes(savedSortColumn)
    ? savedSortColumn
    : "status";

let sortDirection = ["asc", "desc"].includes(savedSortDirection)
    ? savedSortDirection
    : "desc";

function esc(value) {
    if (value === null || value === undefined) {
        return "";
    }

    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}

function isOnline(peer) {
    return peer.status === "running";
}

function endpointHost(endpoint) {
    if (!endpoint) return "";

    const value = String(endpoint).trim();

    if (value.startsWith("[")) {
        const close = value.indexOf("]");
        return close > 1 ? value.slice(1, close) : value;
    }

    const lastColon = value.lastIndexOf(":");
    if (lastColon <= 0) {
        return value;
    }

    const after = value.slice(lastColon + 1);
    return /^\d+$/.test(after)
        ? value.slice(0, lastColon)
        : value;
}

function handshakeSeconds(value) {
    if (!value || value === "No Handshake") {
        return Number.POSITIVE_INFINITY;
    }

    const parts = String(value).split(":");
    if (parts.length !== 3) {
        return Number.POSITIVE_INFINITY;
    }

    const h = Number(parts[0]);
    const m = Number(parts[1]);
    const s = Number(parts[2]);

    if (![h, m, s].every(Number.isFinite)) {
        return Number.POSITIVE_INFINITY;
    }

    return h * 3600 + m * 60 + s;
}

function humanHandshake(value) {
    const total = handshakeSeconds(value);

    if (!Number.isFinite(total)) {
        return "никогда";
    }

    const seconds = Math.max(0, Math.floor(total));

    if (seconds < 60) {
        return `${seconds} сек`;
    }

    if (seconds < 3600) {
        return `${Math.floor(seconds / 60)} мин`;
    }

    if (seconds < 86400) {
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        return m ? `${h} ч ${m} мин` : `${h} ч`;
    }

    const d = Math.floor(seconds / 86400);
    const h = Math.floor((seconds % 86400) / 3600);
    return h ? `${d} д ${h} ч` : `${d} д`;
}

function formatTraffic(value) {
    const n = Number(value);

    if (!Number.isFinite(n)) {
        return "—";
    }

    if (n < 0.001) {
        return `${(n * 1024 * 1024).toFixed(1)} KB`;
    }

    if (n < 1) {
        return `${(n * 1024).toFixed(1)} MB`;
    }

    return `${n.toFixed(n >= 10 ? 1 : 2)} GB`;
}

function trafficValue(peer) {
    const receive = Number(peer.receive);
    const sent = Number(peer.sent);
    return (Number.isFinite(receive) ? receive : 0)
        + (Number.isFinite(sent) ? sent : 0);
}

function ipv4ToNumber(value) {
    if (!value) return Number.MAX_SAFE_INTEGER;

    const parts = String(value).split(".").map(Number);
    if (parts.length !== 4 || parts.some(x => !Number.isInteger(x) || x < 0 || x > 255)) {
        return Number.MAX_SAFE_INTEGER;
    }

    return (((parts[0] * 256 + parts[1]) * 256 + parts[2]) * 256 + parts[3]);
}

function groupText(peer) {
    return (peer.groups || []).join(", ");
}

function comparePeers(a, b) {
    let left;
    let right;

    switch (sortColumn) {
        case "status":
            left = isOnline(a) ? 1 : 0;
            right = isOnline(b) ? 1 : 0;
            break;

        case "name":
            left = a.name || "";
            right = b.name || "";
            break;

        case "ip":
            left = ipv4ToNumber(a.vpn_ip);
            right = ipv4ToNumber(b.vpn_ip);
            break;

        case "handshake":
            left = handshakeSeconds(a.latest_handshake);
            right = handshakeSeconds(b.latest_handshake);
            break;

        case "traffic":
            left = trafficValue(a);
            right = trafficValue(b);
            break;

        case "groups":
            left = groupText(a);
            right = groupText(b);
            break;

        default:
            return 0;
    }

    let result = 0;

    if (typeof left === "string" || typeof right === "string") {
        result = String(left).localeCompare(
            String(right),
            "ru",
            { sensitivity: "base", numeric: true }
        );
    } else {
        result = left < right ? -1 : left > right ? 1 : 0;
    }

    if (result === 0 && sortColumn !== "name") {
        result = String(a.name || "").localeCompare(
            String(b.name || ""),
            "ru",
            { sensitivity: "base", numeric: true }
        );
    }

    return sortDirection === "asc" ? result : -result;
}

function setTheme(theme) {
    const normalized = theme === "dark" ? "dark" : "light";
    document.documentElement.dataset.theme = normalized;
    localStorage.setItem("vpnMonitor.theme", normalized);

    const icon = document.getElementById("themeIcon");
    if (icon) {
        icon.textContent = normalized === "dark" ? "☀" : "☾";
    }

    const toggle = document.getElementById("themeToggle");
    if (toggle) {
        toggle.title = normalized === "dark"
            ? "Включить светлую тему"
            : "Включить тёмную тему";
    }
}

function initTheme() {
    const current = document.documentElement.dataset.theme || "light";
    setTheme(current);
}

async function copyText(value) {
    const text = String(value ?? "");

    if (!text) {
        return false;
    }

    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch (_) {
        // fallback below
    }

    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    area.style.pointerEvents = "none";
    document.body.appendChild(area);
    area.select();

    let ok = false;
    try {
        ok = document.execCommand("copy");
    } finally {
        area.remove();
    }

    return ok;
}

async function copyElement(element) {
    const value = element.dataset.copy;
    if (!value) return;

    const original = element.dataset.originalText || element.textContent;
    element.dataset.originalText = original;

    if (await copyText(value)) {
        element.textContent = `${original} ✓`;
        element.classList.add("copied");

        window.setTimeout(() => {
            element.textContent = original;
            element.classList.remove("copied");
        }, 900);
    }
}

function applyAccessUi() {
    const userBadge = document.getElementById("userBadge");
    const accessInfo = document.getElementById("accessInfo");
    const allGroup = document.getElementById("allGroup");
    const clientsTitle = document.getElementById("clientsTitle");
    const groupsTitle = document.getElementById("groupsTitle");
    const restrictedHint = document.getElementById("restrictedHint");

    if (me.is_admin) {
        userBadge.textContent = `${me.username} · admin`;
        accessInfo.textContent = "Полный доступ";
        allGroup.classList.remove("hidden");
        clientsTitle.textContent = "Клиенты";
        groupsTitle.textContent = "Группы";
        restrictedHint.classList.add("hidden");
        return;
    }

    userBadge.textContent = `${me.username} · ${me.group || me.username}`;
    accessInfo.textContent = `Доступ: ${me.group || me.username}`;
    allGroup.classList.add("hidden");
    clientsTitle.textContent = "Доступ";
    groupsTitle.textContent = "Ваша группа";
    restrictedHint.classList.remove("hidden");

    const allowed = me.group || me.username;
    selectedGroup = allowed;
    localStorage.setItem("vpnMonitor.selectedGroup", allowed);
}

function filteredPeers() {
    const q = searchText.trim().toLocaleLowerCase("ru");

    return peers
        .filter(peer => {
            if (!me.is_admin) {
                const allowed = me.group || me.username;
                if (!(peer.groups || []).some(name =>
                    String(name).localeCompare(
                        allowed,
                        "ru",
                        { sensitivity: "base" }
                    ) === 0
                )) {
                    return false;
                }
            }

            if (selectedGroup) {
                const inGroup = (peer.groups || []).some(name =>
                    String(name).localeCompare(
                        selectedGroup,
                        "ru",
                        { sensitivity: "base" }
                    ) === 0
                );

                if (!inGroup) return false;
            }

            if (selectedFilter === "online" && !isOnline(peer)) {
                return false;
            }

            if (selectedFilter === "offline" && isOnline(peer)) {
                return false;
            }

            if (q) {
                const haystack = [
                    peer.name,
                    peer.notes,
                    peer.vpn_ip,
                    endpointHost(peer.endpoint),
                    ...(peer.groups || [])
                ]
                    .filter(Boolean)
                    .join(" ")
                    .toLocaleLowerCase("ru");

                if (!q.split(/\s+/u).filter(Boolean).every(word => haystack.includes(word))) {
                    return false;
                }
            }

            return true;
        })
        .sort(comparePeers);
}

function renderGroups() {
    const container = document.getElementById("groups");
    container.innerHTML = "";

    const visibleGroups = [...groups].sort((a, b) =>
        String(a.name || "").localeCompare(
            String(b.name || ""),
            "ru",
            { sensitivity: "base", numeric: true }
        )
    );

    if (!me.is_admin) {
        const allowed = me.group || me.username;
        selectedGroup = allowed;
    } else if (
        selectedGroup &&
        !visibleGroups.some(g =>
            String(g.name || "").localeCompare(
                selectedGroup,
                "ru",
                { sensitivity: "base" }
            ) === 0
        )
    ) {
        selectedGroup = null;
        localStorage.removeItem("vpnMonitor.selectedGroup");
    }

    for (const group of visibleGroups) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "sidebar-item";
        button.dataset.group = group.name || "";

        const groupPeers = peers.filter(peer =>
            (peer.groups || []).some(name =>
                String(name).localeCompare(
                    group.name || "",
                    "ru",
                    { sensitivity: "base" }
                ) === 0
            )
        );

        const totalCount = groupPeers.length;
        const onlineCount = groupPeers.filter(isOnline).length;

        button.innerHTML = `
            <span>${esc(group.name || "Без названия")}</span>
            <span class="sidebar-count"
                  title="Online / всего в группе">${onlineCount}/${totalCount}</span>
        `;

        if (
            selectedGroup &&
            String(group.name || "").localeCompare(
                selectedGroup,
                "ru",
                { sensitivity: "base" }
            ) === 0
        ) {
            button.classList.add("active");
        }

        button.addEventListener("click", () => {
            selectedGroup = group.name || null;
            localStorage.setItem("vpnMonitor.selectedGroup", selectedGroup || "");
            render();
        });

        container.appendChild(button);
    }

    const allButton = document.getElementById("allGroup");
    allButton.classList.toggle(
        "active",
        me.is_admin && !selectedGroup
    );

    document.getElementById("allCount").textContent = peers.length;
}

function renderPeers() {
    const list = document.getElementById("peerList");
    const empty = document.getElementById("emptyState");

    const items = filteredPeers();
    list.innerHTML = "";

    empty.classList.toggle("hidden", items.length !== 0);

    for (const peer of items) {
        const row = document.createElement("div");
        row.className = "peer-row";
        row.dataset.peerId = peer.id || "";

        if (selectedPeerId && selectedPeerId === peer.id) {
            row.classList.add("selected");
        }

        const online = isOnline(peer);

        row.innerHTML = `
            <div class="col-status status-cell">
                <span class="status-dot ${online ? "online" : "offline"}"></span>
                <span>${online ? "Online" : "Offline"}</span>
            </div>

            <div class="col-name peer-name">
                <span class="copy-name"
                      data-copy="${esc(peer.name || "")}"
                      title="Нажмите, чтобы скопировать имя">${esc(peer.name || "—")}</span>
                ${
                    peer.notes
                        ? `<span class="peer-note" title="${esc(peer.notes)}">${esc(peer.notes)}</span>`
                        : ""
                }
            </div>

            <div class="col-ip peer-ip">
                ${
                    peer.vpn_ip
                        ? `<span class="copy-ip"
                                 data-copy="${esc(peer.vpn_ip)}"
                                 title="Нажмите, чтобы скопировать IP">${esc(peer.vpn_ip)}</span>`
                        : `<span class="muted">—</span>`
                }
            </div>

            <div class="col-handshake muted">
                ${esc(humanHandshake(peer.latest_handshake))}
            </div>

            <div class="col-traffic traffic">
                <span>↓ ${esc(formatTraffic(peer.receive))}</span>
                <span>↑ ${esc(formatTraffic(peer.sent))}</span>
            </div>

            <div class="col-tags tags">
                ${
                    (peer.groups || []).length
                        ? (peer.groups || [])
                            .map(group => `<span class="tag">${esc(group)}</span>`)
                            .join("")
                        : `<span class="muted">—</span>`
                }
            </div>
        `;

        row.addEventListener("click", event => {
            if (event.target.closest(".copy-ip, .copy-name")) {
                return;
            }

            selectedPeerId = peer.id || null;
            openPeer(peer);
            renderPeers();
        });

        row.querySelectorAll(".copy-ip, .copy-name").forEach(element => {
            element.addEventListener("click", async event => {
                event.stopPropagation();
                await copyElement(element);
            });
        });

        list.appendChild(row);
    }
}

function renderHeader() {
    const total = peers.length;
    const online = peers.filter(isOnline).length;

    document.getElementById("headerTotal").textContent = total;
    document.getElementById("headerOnline").textContent = online;
}

function renderFilters() {
    document.querySelectorAll(".filter").forEach(button => {
        button.classList.toggle(
            "active",
            button.dataset.filter === selectedFilter
        );
    });
}

function render() {
    renderHeader();
    renderGroups();
    renderFilters();
    renderPeers();
}

function detailRow(name, value) {
    return `
        <div class="detail-row">
            <div class="detail-name">${esc(name)}</div>
            <div class="detail-value">${value}</div>
        </div>
    `;
}

function openPeer(peer) {
    selectedPeer = peer;
    const endpointIp = endpointHost(peer.endpoint);

    document.getElementById("modalTitle").textContent = peer.name || "Клиент";

    document.getElementById("modalBody").innerHTML = [
        detailRow(
            "Статус",
            `<span class="status-cell">
                <span class="status-dot ${isOnline(peer) ? "online" : "offline"}"></span>
                <span>${isOnline(peer) ? "Online" : "Offline"}</span>
             </span>`
        ),
        detailRow(
            "Имя",
            peer.name
                ? `<span class="copy-name"
                         data-copy="${esc(peer.name)}"
                         title="Нажмите, чтобы скопировать">${esc(peer.name)}</span>`
                : "—"
        ),
        detailRow(
            "VPN IP",
            peer.vpn_ip
                ? `<span class="copy-ip"
                         data-copy="${esc(peer.vpn_ip)}"
                         title="Нажмите, чтобы скопировать">${esc(peer.vpn_ip)}</span>`
                : "—"
        ),
        detailRow(
            "External IP",
            endpointIp
                ? `<span class="copy-ip"
                         data-copy="${esc(endpointIp)}"
                         title="Нажмите, чтобы скопировать">${esc(endpointIp)}</span>`
                : "—"
        ),
        detailRow("Endpoint", esc(peer.endpoint || "—")),
        detailRow("Handshake", esc(humanHandshake(peer.latest_handshake))),
        detailRow("Получено", esc(formatTraffic(peer.receive))),
        detailRow("Отправлено", esc(formatTraffic(peer.sent))),
        detailRow(
            "Группы",
            (peer.groups || []).length
                ? `<div class="tags">${(peer.groups || [])
                    .map(group => `<span class="tag">${esc(group)}</span>`)
                    .join("")}</div>`
                : "—"
        ),
        detailRow("Заметка", esc(peer.notes || "—"))
    ].join("");

    document
        .querySelectorAll("#modalBody .copy-ip, #modalBody .copy-name")
        .forEach(element => {
            element.addEventListener("click", () => copyElement(element));
        });

    const modalBg = document.getElementById("modalBg");
    modalBg.classList.add("show");
    modalBg.setAttribute("aria-hidden", "false");
}

function closeModal() {
    document.getElementById("modalBg").classList.remove("show");
    document.getElementById("modalBg").setAttribute("aria-hidden", "true");
    selectedPeer = null;
}

function updateSortIndicators() {
    document.querySelectorAll(".sortable").forEach(header => {
        const arrow = header.querySelector(".sort-arrow");
        if (!arrow) return;

        arrow.textContent = header.dataset.sort === sortColumn
            ? (sortDirection === "asc" ? "↑" : "↓")
            : "";
    });
}

async function loadIdentity() {
    const response = await fetch("/api/v1/me", { cache: "no-store" });

    if (!response.ok) {
        throw new Error(`Не удалось получить пользователя: HTTP ${response.status}`);
    }

    me = await response.json();
    applyAccessUi();
}

async function updateData() {
    try {
        const response = await fetch("/api/status", { cache: "no-store" });

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const data = await response.json();

        peers = Array.isArray(data.peers) ? data.peers : [];
        groups = Array.isArray(data.groups) ? data.groups : [];

        document.getElementById("configName").textContent =
            data.configuration || "—";

        document.getElementById("updated").textContent =
            `Обновление: ${data.updated || "—"}`;

        const error = document.getElementById("error");

        if (data.error) {
            error.textContent = data.error;
            error.classList.remove("hidden");
        } else {
            error.classList.add("hidden");
            error.textContent = "";
        }

        if (!me.is_admin) {
            selectedGroup = me.group || me.username;
        }

        render();
    } catch (error) {
        const box = document.getElementById("error");
        box.textContent = `Ошибка обновления: ${error.message || error}`;
        box.classList.remove("hidden");
    }
}

document.getElementById("themeToggle").addEventListener("click", () => {
    const current = document.documentElement.dataset.theme;
    setTheme(current === "dark" ? "light" : "dark");
});

document.getElementById("allGroup").addEventListener("click", () => {
    if (!me.is_admin) {
        return;
    }

    selectedGroup = null;
    localStorage.removeItem("vpnMonitor.selectedGroup");
    render();
});

document.querySelectorAll(".filter").forEach(button => {
    button.addEventListener("click", () => {
        selectedFilter = button.dataset.filter || "all";
        localStorage.setItem("vpnMonitor.selectedFilter", selectedFilter);
        render();
    });
});

document.getElementById("searchInput").addEventListener("input", event => {
    searchText = event.target.value || "";
    renderPeers();
});

document.getElementById("modalClose").addEventListener("click", closeModal);

document.getElementById("modalBg").addEventListener("click", event => {
    if (event.target.id === "modalBg") {
        closeModal();
    }
});

document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
        closeModal();
    }
});

document.querySelectorAll(".sortable").forEach(header => {
    header.addEventListener("click", () => {
        const column = header.dataset.sort;

        if (sortColumn === column) {
            sortDirection = sortDirection === "asc" ? "desc" : "asc";
        } else {
            sortColumn = column;
            sortDirection = column === "status" ? "desc" : "asc";
        }

        localStorage.setItem("vpnMonitor.sortColumn", sortColumn);
        localStorage.setItem("vpnMonitor.sortDirection", sortDirection);

        updateSortIndicators();
        renderPeers();
    });
});

async function start() {
    initTheme();

    if (!["all", "online", "offline"].includes(selectedFilter)) {
        selectedFilter = "all";
    }

    updateSortIndicators();

    try {
        await loadIdentity();
        document.getElementById("usersButton").classList.toggle("hidden", !me.is_admin);
        await updateData();
    } catch (error) {
        const box = document.getElementById("error");
        box.textContent = `Ошибка запуска: ${error.message || error}`;
        box.classList.remove("hidden");
    }

    window.setInterval(updateData, 5000);
}

start();
