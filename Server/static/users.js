(() => {
    const $ = id => document.getElementById(id);
    let token = "", editing = null, deleting = null, busy = false;
    const dialog = $("usersDialog");
    function reset() {
        editing = null; $("userForm").reset(); $("userName").disabled = false;
        $("userPassword").required = true; $("userPassword").placeholder = "";
        $("userFormTitle").textContent = "Новый пользователь"; $("userSave").textContent = "Создать";
    }
    async function api(path, method = "GET", body) {
        const response = await fetch("/api/v1/admin/users" + path, {
            method, cache: "no-store", credentials: "same-origin", signal: AbortSignal.timeout(15000),
            headers: method === "GET" ? {} : { "Content-Type": "application/json", "X-VpnMonitor-CSRF": token },
            body: body === undefined ? undefined : JSON.stringify(body)
        });
        const value = await response.json();
        if (!response.ok) throw new Error(typeof value.detail === "string" ? value.detail : "Не удалось выполнить запрос");
        return value;
    }
    function button(text, action) {
        const b = document.createElement("button"); b.type = "button"; b.textContent = text;
        b.addEventListener("click", action); return b;
    }
    async function load() {
        const value = await api(""); token = value.csrf_token;
        $("usersRows").replaceChildren(); $("userGroups").replaceChildren();
        for (const name of value.groups) { const option = document.createElement("option"); option.value = name; $("userGroups").append(option); }
        for (const user of value.items) {
            const row = document.createElement("tr");
            for (const text of [user.username, user.is_admin ? "Все группы · администратор" : user.group_exists ? user.group : "Группа не найдена — список клиентов пуст", user.enabled ? "Вход разрешён" : "Заблокирован"]) {
                const cell = document.createElement("td"); cell.textContent = text; row.append(cell);
            }
            const actions = document.createElement("td");
            if (user.is_admin) actions.textContent = "Настройки сервера";
            else {
                actions.append(button("Изменить", () => {
                    if (busy) return; editing = user.username; $("userName").value = user.username; $("userName").disabled = true;
                    $("userPassword").value = ""; $("userPassword").required = false; $("userPassword").placeholder = "Пусто — оставить прежний";
                    $("userEnabled").checked = user.enabled; $("userFormTitle").textContent = "Изменить пользователя"; $("userSave").textContent = "Сохранить";
                    $("userPassword").focus();
                }), button("Удалить", () => {
                    if (busy) return; deleting = user.username; $("userDeleteText").textContent = `Удалить учётную запись «${user.username}»?`;
                    $("userDeleteDialog").showModal(); $("userDeleteCancel").focus();
                }));
            }
            row.append(actions); $("usersRows").append(row);
        }
    }
    async function run(action) {
        if (busy) return; busy = true; $("userSave").disabled = true; $("usersRefresh").disabled = true;
        $("usersError").textContent = "";
        try { await action(); } catch (error) { $("usersError").textContent = error.name === "TimeoutError" || error.name === "AbortError" ? "Сервер не ответил. Обновите список перед повтором." : error.message || "Ошибка операции"; }
        finally { busy = false; $("userSave").disabled = false; $("usersRefresh").disabled = false; }
    }
    $("usersButton").addEventListener("click", () => { reset(); dialog.showModal(); run(load); });
    $("usersRefresh").addEventListener("click", () => run(load));
    $("usersClose").addEventListener("click", () => { if (!busy) dialog.close(); });
    dialog.addEventListener("close", () => { $("userPassword").value = ""; token = ""; });
    dialog.addEventListener("cancel", event => { if (busy) event.preventDefault(); });
    $("userReset").addEventListener("click", () => { if (!busy) reset(); });
    $("userForm").addEventListener("submit", event => {
        event.preventDefault();
        const name = $("userName").value.trim(), password = $("userPassword").value, enabled = $("userEnabled").checked;
        const current = editing;
        run(async () => {
            const body = { enabled }; if (!current) body.username = name; if (password || !current) body.password = password;
            await api(current ? "/" + encodeURIComponent(current) : "", current ? "PATCH" : "POST", body);
            reset(); await load(); $("usersError").textContent = "Сохранено.";
        });
    });
    $("userDeleteCancel").addEventListener("click", () => $("userDeleteDialog").close());
    $("userDeleteConfirm").addEventListener("click", () => {
        const name = deleting; $("userDeleteDialog").close();
        run(async () => { await api("/" + encodeURIComponent(name), "DELETE"); reset(); await load(); $("usersError").textContent = "Пользователь удалён."; });
    });
})();
