# VPN Monitor — multi-user server patch v1

## Логика доступа

- Администратор берётся из существующих `AUTH_USER` / `AUTH_PASSWORD` в `/etc/vpn-monitor.env` и видит всё.
- Обычные пользователи хранятся в `/etc/vpn-monitor-users.json`.
- Пароли обычных пользователей не хранятся открытым текстом: используется `scrypt` + индивидуальная случайная salt.
- Обычный пользователь видит только группу, имя которой совпадает с его логином без учёта регистра, и только peer из этой группы.
- Если peer входит в несколько групп, обычному пользователю в поле `groups` возвращается только его разрешённая группа.
- Если группу переименовать или изменить её состав в WGDashboard, доступ меняется автоматически на следующем обновлении данных; перезапуск не нужен.
- Если группы с именем пользователя нет, пользователь получает пустой список.

## Новые API

`GET /api/v1/me`:

```json
{"username":"Офис","is_admin":false,"group":"Офис"}
```

Все существующие API (`/api/status`, `/api/v1/summary`, `/api/v1/peers`, `/api/v1/groups`, `/api/v1/peers/{id}`) фильтруются на сервере.

## Установка

Сначала сделайте штатный backup:

```bash
sudo systemctl start vpn-monitor-backup.service
sudo systemctl status vpn-monitor-backup.service --no-pager
```

Скопируйте из этого архива `app.py`, `auth_store.py`, `vpn-monitor-user` на сервер, затем:

```bash
cd /путь/куда/распаковали/vpn-monitor-server-multiuser-v1

sudo install -m 0644 app.py /opt/vpn-monitor/app.py
sudo install -m 0644 auth_store.py /opt/vpn-monitor/auth_store.py
sudo install -m 0755 vpn-monitor-user /usr/local/sbin/vpn-monitor-user

sudo vpn-monitor-user init
sudo systemctl restart vpn-monitor.service
sudo systemctl status vpn-monitor.service --no-pager
```

Проверка администратора:

```bash
curl -u admin http://127.0.0.1/api/v1/me
curl -u admin http://127.0.0.1/api/v1/summary
```

`curl -u admin` запросит пароль интерактивно и не оставит его в истории shell.

## Добавление пользователя

Имя пользователя должно совпадать с названием группы WGDashboard. Например:

```bash
sudo vpn-monitor-user add 'Офис'
```

Команда дважды запросит пароль.

Проверка:

```bash
curl -u 'Офис' http://127.0.0.1/api/v1/me
curl -u 'Офис' http://127.0.0.1/api/v1/groups
curl -u 'Офис' http://127.0.0.1/api/v1/peers
```

## Управление пользователями

```bash
sudo vpn-monitor-user list
sudo vpn-monitor-user passwd 'Офис'
sudo vpn-monitor-user disable 'Офис'
sudo vpn-monitor-user enable 'Офис'
sudo vpn-monitor-user delete 'Офис'
```

## Backup

Добавьте файл пользователей в существующий backup-скрипт:

```text
etc/vpn-monitor-users.json
```

и модуль/CLI:

```text
opt/vpn-monitor/auth_store.py
usr/local/sbin/vpn-monitor-user
```

Ваш backup уже сохраняет `/opt/vpn-monitor`, поэтому `auth_store.py` попадёт туда автоматически; отдельно важно добавить `/etc/vpn-monitor-users.json` и `/usr/local/sbin/vpn-monitor-user`, если CLI ещё не входит в список.
